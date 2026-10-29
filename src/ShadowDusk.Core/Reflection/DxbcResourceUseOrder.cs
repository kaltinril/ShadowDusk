#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The order in which a compiled SM4/SM5 shader first reads each texture register: the
/// <c>tN</c> operands of its texture-reading instructions (<c>sample</c> and its family,
/// <c>ld</c>, <c>resinfo</c>, <c>gather4</c> ...) in instruction-stream order, each register
/// once. Pure managed; reads the <c>SHDR</c>/<c>SHEX</c> chunk of a DXBC container.
///
/// <para>Why it exists: fxc gives a LEGACY sampler's texture (<c>sampler2D A;</c> sampled with
/// <c>tex2D</c>) its <c>tN</c> slot in the order the shader first reads it, which is the order
/// the compiler evaluates the reads in (an argument before the call that takes it, an inner
/// read before the dependent read around it). ShadowDusk compiles the SM4 rewrite of such a
/// shader, whose backend numbers the rewritten <c>Texture2D</c> declarations in declaration
/// order instead; the instruction stream of that first compile carries the evaluation order
/// the backend emitted, which is what the DirectX 11 slot re-assignment reads.</para>
///
/// <para>Only the texture-reading opcodes are decoded, operand by operand, so a declaration's
/// raw payload is never mistaken for an operand; every other instruction is skipped by its
/// length. Anything unexpected (not a DXBC container, no shader chunk, a length that runs past
/// the chunk, an operand that runs past its instruction) returns <c>null</c>: the caller then
/// keeps the slots the backend chose.</para>
/// </summary>
internal static class DxbcResourceUseOrder
{
    private const uint DxbcFourcc = 0x43425844; // 'DXBC'
    private const uint ShdrFourcc = 0x52444853; // 'SHDR'
    private const uint ShexFourcc = 0x58454853; // 'SHEX'

    private const int OpcodeCustomData = 53;

    // D3D10_SB_OPERAND_TYPE values.
    private const uint OperandImmediate32 = 4;
    private const uint OperandImmediate64 = 5;
    private const uint OperandResource    = 7;

    /// <summary>
    /// The opcodes whose operands are decoded: every instruction that reads through a texture
    /// register (D3D10/10.1/11 tokenized program format numbering).
    /// </summary>
    private static readonly HashSet<int> TextureReadOpcodes = new()
    {
        45,  // ld
        46,  // ld_ms
        65,  // resinfo
        69,  // sample
        70,  // sample_c
        71,  // sample_c_lz
        72,  // sample_l
        73,  // sample_d
        74,  // sample_b
        108, // lod
        109, // gather4
        111, // sample_info
        121, // bufinfo
        126, // gather4_c
        127, // gather4_po
        128, // gather4_po_c
        165, // ld_raw
        167, // ld_structured
    };

    /// <summary>
    /// The texture registers <paramref name="dxbc"/> reads, in order of first read; <c>null</c>
    /// when the blob cannot be walked (see the type remarks).
    /// </summary>
    public static IReadOnlyList<int>? TextureRegistersInFirstReadOrder(ReadOnlySpan<byte> dxbc)
    {
        ReadOnlySpan<byte> program = FindShaderChunk(dxbc);
        if (program.IsEmpty || program.Length < 8)
            return null;

        int totalDwords = (int)Math.Min(ReadU32(program, 4), (uint)(program.Length / 4));
        var order = new List<int>();
        var seen = new HashSet<int>();

        int pos = 2; // version token, length token
        while (pos < totalDwords)
        {
            uint opcodeToken = ReadU32(program, pos * 4);
            int opcode = (int)(opcodeToken & 0x7FF);
            int length;
            if (opcode == OpcodeCustomData)
            {
                if (pos + 1 >= totalDwords)
                    return null;
                length = (int)ReadU32(program, (pos + 1) * 4);
            }
            else
            {
                length = (int)((opcodeToken >> 24) & 0x7F);
            }
            if (length <= 0 || pos + length > totalDwords)
                return null;

            if (TextureReadOpcodes.Contains(opcode))
            {
                int end = pos + length;
                int cursor = pos + 1;
                // Extended opcode tokens (e.g. the immediate texel offset of a sample).
                bool extended = (opcodeToken & 0x80000000u) != 0;
                while (extended)
                {
                    if (cursor >= end)
                        return null;
                    extended = (ReadU32(program, cursor * 4) & 0x80000000u) != 0;
                    cursor++;
                }
                while (cursor < end)
                {
                    if (!ReadOperand(program, ref cursor, end, out uint type, out int register))
                        return null;
                    if (type == OperandResource && register >= 0 && seen.Add(register))
                        order.Add(register);
                }
            }

            pos += length;
        }

        return order;
    }

    /// <summary>
    /// Decodes one operand at <paramref name="cursor"/> (advancing past it, its extended tokens,
    /// immediates and every index, relative ones included). <paramref name="register"/> is the
    /// immediate first index, or -1 when there is none.
    /// </summary>
    private static bool ReadOperand(ReadOnlySpan<byte> program, ref int cursor, int end, out uint type, out int register)
    {
        type = 0;
        register = -1;
        if (cursor >= end)
            return false;

        uint token = ReadU32(program, cursor * 4);
        cursor++;
        uint components = token & 3;
        type = (token >> 12) & 0xFF;
        int indexDimension = (int)((token >> 20) & 3);

        bool extended = (token & 0x80000000u) != 0;
        while (extended)
        {
            if (cursor >= end)
                return false;
            extended = (ReadU32(program, cursor * 4) & 0x80000000u) != 0;
            cursor++;
        }

        if (type == OperandImmediate32)
            cursor += components == 1 ? 1 : 4;
        else if (type == OperandImmediate64)
            cursor += components == 1 ? 2 : 8;
        if (cursor > end)
            return false;

        for (int i = 0; i < indexDimension; i++)
        {
            uint representation = (token >> (22 + 3 * i)) & 7;
            switch (representation)
            {
                case 0: // immediate32
                    if (cursor >= end)
                        return false;
                    if (i == 0)
                        register = (int)ReadU32(program, cursor * 4);
                    cursor += 1;
                    break;
                case 1: // immediate64
                    cursor += 2;
                    break;
                case 2: // relative
                    if (!ReadOperand(program, ref cursor, end, out _, out _))
                        return false;
                    break;
                case 3: // immediate32 + relative
                    cursor += 1;
                    if (!ReadOperand(program, ref cursor, end, out _, out _))
                        return false;
                    break;
                case 4: // immediate64 + relative
                    cursor += 2;
                    if (!ReadOperand(program, ref cursor, end, out _, out _))
                        return false;
                    break;
                default:
                    return false;
            }
            if (cursor > end)
                return false;
        }

        return true;
    }

    private static ReadOnlySpan<byte> FindShaderChunk(ReadOnlySpan<byte> dxbc)
    {
        if (dxbc.Length < 32 || ReadU32(dxbc, 0) != DxbcFourcc)
            return default;
        uint chunkCount = ReadU32(dxbc, 28);
        if (chunkCount > 1024 || 32 + chunkCount * 4L > dxbc.Length)
            return default;
        for (int i = 0; i < chunkCount; i++)
        {
            uint offset = ReadU32(dxbc, 32 + i * 4);
            if (offset + 8L > dxbc.Length)
                return default;
            uint fourcc = ReadU32(dxbc, (int)offset);
            uint size = ReadU32(dxbc, (int)offset + 4);
            if (offset + 8L + size > dxbc.Length)
                return default;
            if (fourcc is ShdrFourcc or ShexFourcc)
                return dxbc.Slice((int)offset + 8, (int)size);
        }
        return default;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
}
