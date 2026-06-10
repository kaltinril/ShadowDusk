#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ShadowDusk.Core;

/// <summary>
/// Post-pass over vkd3d's D3D9 SM2/SM3 token streams that canonicalizes the two
/// instruction forms MojoShader rejects but vkd3d 1.17 emits (found by the Phase 39
/// rung-3/4 FNA harness — real FNA load failures where the fxc oracle loads fine):
///
///   1. <c>texkill</c> with a partial destination writemask (vkd3d writes e.g. <c>.x</c>;
///      fxc always writes <c>.xyzw</c> and MojoShader hard-fails anything else:
///      "TEXKILL writemask must be .xyzw").
///   2. <c>texld</c> whose coordinate source MojoShader rejects: a swizzle below SM3
///      ("TEXLD src0 must not swizzle" for ps_1_x/ps_2_x; SM3 allows it), or a source
///      modifier at ANY SM2+ major ("TEXLD src0 must have no modifiers").
///   3. <c>def</c> float literals with |f| ≥ 2³² — vkd3d's <c>discard</c> sentinel is
///      −2³² (0xCF800000), and MojoShader's <c>MOJOSHADER_printFloat</c> converts the
///      magnitude through a 32-bit <c>unsigned long</c> on Windows (LLP64), overflowing
///      to <c>±0.0</c> in the translated source — so <c>texkill</c>'s <c>&lt; 0</c> test
///      never fires (Dissolve rendered un-discarded). Clamped in place to the same-signed
///      largest float BELOW 2³² (±4294967040.0, 0x4F7FFFFF), which MojoShader prints
///      exactly; the sentinel's only observable property is its sign. fxc's largest def
///      literals are ±1, so the oracle never trips this. Empirically proven
///      pixel-identical in real FNA by the rung-4 harness's clamped-Dissolve experiment.
///
/// The rewrite is semantics-preserving, not a blind mask/swizzle flip: the offending
/// operand is routed through a fresh temporary —
/// <c>mov rK, reg.&lt;replicated-masked-components&gt;</c> followed by the canonical
/// <c>texkill rK.xyzw</c> (each tested lane now holds one of the originally-tested
/// values), or <c>mov rK, src0.&lt;swizzle&gt;</c> followed by <c>texld dst, rK, s#</c>.
/// Blindly widening the texkill mask would test garbage lanes; this never does.
///
/// D3D9 SM2+ instruction tokens carry an explicit operand-count field and the format
/// has no byte-offset branches, so inserting instructions is purely mechanical; comment
/// blocks (the CTAB) pass through byte-identical. Streams below SM2 are returned
/// unchanged: MojoShader's ps_1_x rules differ wholesale (and ps_1_x tokens carry no
/// instruction-length fields to walk), and the FNA pipeline rejects literal SM1
/// profiles upstream (SD0300), so this pass never has to reason about them.
///
/// Applied ONLY on the FNA path — the DirectX SM5 path never sees this code, and the
/// blob handed to the fx_2_0 writer stays exactly what MojoShader will consume.
/// </summary>
public static class D3d9BytecodePatcher
{
    private const uint EndToken = 0x0000FFFF;
    private const uint CommentOpcode = 0x0000FFFE;

    private const int OpMov = 0x01;
    private const int OpDcl = 0x1F;
    private const int OpDefB = 0x2F;
    private const int OpDefI = 0x30;
    private const int OpTexKill = 0x41;
    private const int OpTexLd = 0x42;
    private const int OpDef = 0x51;

    private const uint FullWritemask = 0x000F0000;
    private const uint ParamTokenBit = 0x8000_0000;
    private const uint RelativeAddressingBit = 0x0000_2000;

    /// <summary>
    /// Patches <paramref name="bytecode"/> for MojoShader compatibility. Returns the
    /// original array when nothing needs patching (the common case — zero-copy).
    /// </summary>
    public static Result<byte[], ShaderError> PatchForMojoShader(byte[] bytecode, string sourceFile)
    {
        if (bytecode.Length < 8 || bytecode.Length % 4 != 0)
            return Ok(bytecode);

        uint version = ReadU32(bytecode, 0);
        uint kind = version >> 16;
        if (kind != 0xFFFF && kind != 0xFFFE)
            return Ok(bytecode); // not a D3D9 token stream

        int major = (int)((version >> 8) & 0xFF);
        if (major < 2)
        {
            // SM1.x: MojoShader's ps_1_x rules differ wholesale (and there are no
            // instruction-length fields to walk); the FNA pipeline rejects literal
            // SM1 profiles upstream (SD0300) — leave untouched.
            return Ok(bytecode);
        }

        // ---- Pass 1: find fixup sites and the highest temp register in use.
        var sites = new List<int>(); // byte offset of each instruction token needing a fix
        bool needsDefClamp = false;
        int maxTemp = -1;

        int pos = 4;
        while (pos + 4 <= bytecode.Length)
        {
            uint token = ReadU32(bytecode, pos);
            if (token == EndToken)
                break;

            if ((token & 0xFFFF) == CommentOpcode && (token & ParamTokenBit) == 0)
            {
                pos += 4 + (int)((token >> 16) & 0x7FFF) * 4;
                continue;
            }

            int opcode = (int)(token & 0xFFFF);
            int operandTokens = (int)((token >> 24) & 0xF);
            bool predicated = (token & 0x1000_0000) != 0;
            int instructionStart = pos;
            pos += 4;

            // Truncated tail (operand list runs off the end of the array): stop scanning
            // here — if a patch is still pending, pass 2 fails loudly at this same point.
            if (pos + operandTokens * 4 > bytecode.Length)
                break;

            if (opcode is OpDef or OpDefI or OpDefB)
            {
                // float def literals: detect the |f| ≥ 2³² values MojoShader misprints
                // (fix #3). defi/defb carry integer payloads — never touched.
                if (opcode == OpDef)
                {
                    for (int c = 1; c < operandTokens && pos + 4 * c + 4 <= bytecode.Length; c++)
                    {
                        if (NeedsLiteralClamp(ReadU32(bytecode, pos + 4 * c)))
                            needsDefClamp = true;
                    }
                }
                pos += operandTokens * 4; // literal payload — not parameter tokens
                continue;
            }

            int operandStart = pos;
            if (opcode == OpDcl)
                operandStart += 4; // DCL's first token is a usage descriptor, not a parameter

            // Track temp-register usage (registers' type bits: [30:28] = low, [12:11] = high).
            for (int o = operandStart; o < pos + operandTokens * 4 && o + 4 <= bytecode.Length; o += 4)
            {
                uint p = ReadU32(bytecode, o);
                if ((p & ParamTokenBit) == 0)
                    continue;
                int regType = (int)(((p >> 28) & 0x7) | (((p >> 11) & 0x3) << 3));
                if (regType == 0 /* D3DSPR_TEMP */)
                    maxTemp = Math.Max(maxTemp, (int)(p & 0x7FF));
            }

            if (operandTokens >= 1)
            {
                if (opcode == OpTexKill)
                {
                    uint dest = ReadU32(bytecode, pos);
                    int regType = (int)(((dest >> 28) & 0x7) | (((dest >> 11) & 0x3) << 3));
                    bool fullMask = (dest & FullWritemask) == FullWritemask;
                    // Canonical form = full mask on a TEMP register; anything else (partial
                    // mask, or a non-temp register MojoShader also dislikes) gets routed
                    // through a fresh temp. A predicated site cannot take that rewrite (the
                    // predicate guards the original instruction, not the inserted mov) —
                    // the SD0305 contract says it fails loudly, never silently skipped.
                    if (!fullMask || regType != 0)
                    {
                        if (predicated)
                            return Fail(sourceFile, "predicated texkill/texld cannot be canonicalized");
                        if ((dest & RelativeAddressingBit) != 0)
                            return Fail(sourceFile, "texkill with relative addressing is not patchable");
                        sites.Add(instructionStart);
                    }
                }
                else if (opcode == OpTexLd && operandTokens >= 3)
                {
                    uint src0 = ReadU32(bytecode, pos + 4);
                    bool nonIdentitySwizzle = (src0 & 0x00FF0000) != 0x00E40000;
                    bool hasModifier = ((src0 >> 24) & 0xF) != 0;
                    // Below SM3 MojoShader rejects any swizzle or modifier on src0; at SM3
                    // swizzles are legal but modifiers stay forbidden ("TEXLD src0 must
                    // have no modifiers" applies to every SM2+ major).
                    if (hasModifier || (major < 3 && nonIdentitySwizzle))
                    {
                        if (predicated)
                            return Fail(sourceFile, "predicated texkill/texld cannot be canonicalized");
                        if ((src0 & RelativeAddressingBit) != 0)
                            return Fail(sourceFile, "texld with relative addressing is not patchable");
                        sites.Add(instructionStart);
                    }
                }
            }

            pos += operandTokens * 4;
        }

        if (sites.Count == 0 && !needsDefClamp)
            return Ok(bytecode);

        // One fresh temp serves every site (each mov fully defines it immediately before
        // its single use). SM2 profiles guarantee 12 temps, SM3 32.
        int tempLimit = major >= 3 ? 32 : 12;
        int patchTemp = maxTemp + 1;
        if (sites.Count > 0 && patchTemp >= tempLimit)
            return Fail(sourceFile,
                $"no free temporary register (r{patchTemp} exceeds the SM{major} limit of {tempLimit}) " +
                "to canonicalize texkill/texld for MojoShader");

        // ---- Pass 2: rebuild with the compensating movs inserted.
        var output = new List<byte>(bytecode.Length + sites.Count * 12);
        int siteIndex = 0;
        pos = 0;
        Append(output, bytecode, 0, 4);
        pos = 4;

        while (pos + 4 <= bytecode.Length)
        {
            uint token = ReadU32(bytecode, pos);

            if (token == EndToken)
                break;

            if ((token & 0xFFFF) == CommentOpcode && (token & ParamTokenBit) == 0)
            {
                int len = 4 + (int)((token >> 16) & 0x7FFF) * 4;
                if (pos + len > bytecode.Length)
                    return Fail(sourceFile,
                        "token stream truncated or desynchronized (comment length field overruns the stream)");
                Append(output, bytecode, pos, len);
                pos += len;
                continue;
            }

            int opcode = (int)(token & 0xFFFF);
            int operandTokens = (int)((token >> 24) & 0xF);
            int instructionLength = 4 + operandTokens * 4;

            // Mirror pass 1's truncation handling — but here a malformed stream cannot be
            // tolerated (bytes are being re-emitted), so it fails instead of breaking.
            if (pos + instructionLength > bytecode.Length)
                return Fail(sourceFile,
                    "token stream truncated or desynchronized (instruction operand list overruns the stream)");

            if (opcode == OpDef)
            {
                // A def always carries dest + literal payload; a zero operand count means
                // the stream is lying — re-emitting would duplicate a dword and desync.
                if (operandTokens == 0)
                    return Fail(sourceFile,
                        "token stream truncated or desynchronized (def declares no operand tokens)");

                // Fix #3: clamp misprintable float literals in place (size-preserving;
                // instruction token + dest token pass through unchanged).
                Append(output, bytecode, pos, 8);
                for (int c = 2; c <= operandTokens; c++)
                {
                    uint literal = ReadU32(bytecode, pos + 4 * c);
                    WriteU32(output, NeedsLiteralClamp(literal)
                        ? (literal & 0x8000_0000) | 0x4F7F_FFFF
                        : literal);
                }
                pos += instructionLength;
                continue;
            }

            if (siteIndex < sites.Count && sites[siteIndex] == pos)
            {
                siteIndex++;

                if (opcode == OpTexKill)
                {
                    uint dest = ReadU32(bytecode, pos + 4);

                    // mov rK, reg.<masked components replicated across all four lanes>
                    WriteU32(output, 0x0200_0000u | OpMov);
                    WriteU32(output, ParamTokenBit | FullWritemask | (uint)patchTemp);
                    WriteU32(output, MakeSourceFromDest(dest, ReplicateSwizzleFromMask(dest)));

                    // texkill rK.xyzw
                    WriteU32(output, token);
                    WriteU32(output, ParamTokenBit | FullWritemask | (uint)patchTemp);
                }
                else // OpTexLd (swizzled src0 below SM3, or src0 modifier at any major)
                {
                    uint dest = ReadU32(bytecode, pos + 4);
                    uint src0 = ReadU32(bytecode, pos + 8);
                    uint src1 = ReadU32(bytecode, pos + 12);

                    // mov rK, src0.<swizzle> (keeps src0's swizzle AND modifier)
                    WriteU32(output, 0x0200_0000u | OpMov);
                    WriteU32(output, ParamTokenBit | FullWritemask | (uint)patchTemp);
                    WriteU32(output, src0);

                    // texld dest, rK, s#  (src0 now an unswizzled, unmodified temp)
                    WriteU32(output, token);
                    WriteU32(output, dest);
                    WriteU32(output, ParamTokenBit | 0x00E40000u | (uint)patchTemp);
                    WriteU32(output, src1);
                }

                pos += instructionLength;
                continue;
            }

            Append(output, bytecode, pos, instructionLength);
            pos += instructionLength;
        }

        // End token + anything after it (fxc pads nothing; copy verbatim for safety).
        Append(output, bytecode, pos, bytecode.Length - pos);

        return Ok(output.ToArray());
    }

    /// <summary>
    /// A finite float whose magnitude is ≥ 2³² — exactly the domain where MojoShader's
    /// 32-bit unsigned-long magnitude conversion overflows and prints <c>±0.0</c>
    /// (fix #3). Infinities/NaNs (≥ 0x7F800000) are left alone.
    /// </summary>
    private static bool NeedsLiteralClamp(uint bits)
    {
        uint magnitude = bits & 0x7FFF_FFFF;
        return magnitude is >= 0x4F80_0000 and < 0x7F80_0000;
    }

    /// <summary>
    /// Builds a source-parameter swizzle that replicates the destination-mask's tested
    /// components across all four lanes (mask .xy → swizzle .xyyy), so a full-mask
    /// texkill tests exactly the originally-tested values.
    /// </summary>
    private static uint ReplicateSwizzleFromMask(uint destToken)
    {
        Span<int> components = stackalloc int[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if ((destToken & (1u << (16 + i))) != 0)
                components[count++] = i;
        }
        if (count == 0)
        {
            components[0] = 0; // degenerate mask — test .x (never produced by vkd3d)
            count = 1;
        }

        uint swizzle = 0;
        for (int lane = 0; lane < 4; lane++)
        {
            int component = components[Math.Min(lane, count - 1)];
            swizzle |= (uint)component << (16 + lane * 2);
        }
        return swizzle;
    }

    /// <summary>Turns a destination token's register reference into a plain source token
    /// (same register type + number, given swizzle, no modifiers).</summary>
    private static uint MakeSourceFromDest(uint destToken, uint swizzle) =>
        ParamTokenBit
        | (destToken & 0x7000_0000)  // register type bits [30:28]
        | (destToken & 0x0000_1800)  // register type bits [12:11]
        | (destToken & 0x0000_07FF)  // register number
        | swizzle;

    private static uint ReadU32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static void WriteU32(List<byte> output, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        output.AddRange(tmp);
    }

    private static void Append(List<byte> output, byte[] source, int offset, int count)
    {
        for (int i = 0; i < count; i++)
            output.Add(source[offset + i]);
    }

    private static Result<byte[], ShaderError> Ok(byte[] bytes) =>
        Result<byte[], ShaderError>.Ok(bytes);

    private static Result<byte[], ShaderError> Fail(string sourceFile, string detail) =>
        Result<byte[], ShaderError>.Fail(new ShaderError(
            File: sourceFile,
            Line: 0,
            Column: 0,
            Code: "SD0305",
            Message: "MojoShader-compatibility patch failed: " + detail));
}
