#nullable enable

using System.Buffers.Binary;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests.Fx2;

/// <summary>
/// Unit tests for <see cref="D3d9BytecodePatcher"/> — the MojoShader-compatibility
/// post-pass over vkd3d's D3D9 token streams (Phase 39 rung-3 fix: texkill partial
/// writemask; texld src0 swizzle below SM3 / src0 modifier at any major; predicated
/// sites and malformed streams fail as SD0305). Token encodings per the D3D9 shader
/// token format (instruction token: opcode[15:0], operand count[27:24]; parameter
/// tokens: bit 31 set, register type split [30:28]+[12:11], writemask [19:16],
/// swizzle [23:16]).
/// </summary>
public sealed class D3d9BytecodePatcherTests
{
    private const uint PsV2 = 0xFFFF0200;
    private const uint PsV3 = 0xFFFF0300;
    private const uint End = 0x0000FFFF;

    private const uint MovToken = 0x02000001;     // mov, 2 operands
    private const uint TexKillToken = 0x01000041; // texkill, 1 operand
    private const uint TexLdToken = 0x03000042;   // texld, 3 operands

    private const uint PredicatedTexKillToken = 0x12000041; // texkill, predicated (bit 28), 2 operands (dest + predicate)
    private const uint PredicateSrc = 0xB0E41000;           // p0.xyzw (type 19 = PREDICATE: bits [30:28] = 3, [12:11] = 2)

    private static uint TempDest(int reg, uint mask) => 0x8000_0000u | (mask << 16) | (uint)reg;
    private static uint TempSrc(int reg, uint swizzle) => 0x8000_0000u | (swizzle << 16) | (uint)reg;
    private static uint InputDest(int reg, uint mask) => 0x9000_0000u | (mask << 16) | (uint)reg; // type 1 = INPUT
    private static uint SamplerSrc(int reg) => 0xA000_0800u | 0x00E4_0000u | (uint)reg;           // type 10 = SAMPLER

    private static byte[] Stream(params uint[] tokens)
    {
        var bytes = new byte[tokens.Length * 4];
        for (int i = 0; i < tokens.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), tokens[i]);
        return bytes;
    }

    private static uint[] Tokens(byte[] bytes)
    {
        var tokens = new uint[bytes.Length / 4];
        for (int i = 0; i < tokens.Length; i++)
            tokens[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        return tokens;
    }

    [Fact]
    public void TexkillPartialMask_RoutedThroughFreshTempWithReplicatedSwizzle()
    {
        // mov r1.y, r0.x ; texkill r1(.y) — the vkd3d shape MojoShader rejects.
        byte[] input = Stream(
            PsV3,
            MovToken, TempDest(1, 0x2), TempSrc(0, 0x00),
            TexKillToken, TempDest(1, 0x2),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        // version, mov, texkill→(mov + texkill), end → 1 + 3 + (3 + 2) + 1
        tokens.Should().HaveCount(10, because: "one compensating mov (3 tokens) is inserted");

        tokens[4].Should().Be(MovToken, because: "a mov precedes the canonicalized texkill");
        tokens[5].Should().Be(TempDest(2, 0xF), because: "the fresh temp is r2 (max used was r1) with a full writemask");
        // Source = r1 with .yyyy (component 1 replicated: lanes 01 01 01 01 = 0x55).
        tokens[6].Should().Be(TempSrc(1, 0x55));
        tokens[7].Should().Be(TexKillToken);
        tokens[8].Should().Be(TempDest(2, 0xF), because: "texkill now targets the fresh temp with .xyzw");
    }

    [Fact]
    public void TexkillFullMaskOnTemp_IsPassthrough()
    {
        byte[] input = Stream(PsV3, TexKillToken, TempDest(0, 0xF), End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(input, because: "a clean stream is returned unchanged, zero-copy");
    }

    [Fact]
    public void TexkillOnInputRegister_IsRoutedThroughTemp()
    {
        // texkill v0.xyzw — full mask but a non-temp register (MojoShader strictness).
        byte[] input = Stream(PsV3, TexKillToken, InputDest(0, 0xF), End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        tokens[1].Should().Be(MovToken);
        tokens[2].Should().Be(TempDest(0, 0xF), because: "no temps were in use, so r0 is fresh");
        tokens[3].Should().Be(0x9000_0000u | (0xE4u << 16), because: "the mov reads v0 with the identity swizzle (full mask → xyzw)");
        tokens[5].Should().Be(TempDest(0, 0xF));
    }

    [Fact]
    public void TexldSrc0Swizzle_AtSm2_IsRoutedThroughTemp()
    {
        // texld r0, r1.xyxx, s0 — swizzle 0x04 (lanes x,y,x,x) is illegal below SM3.
        byte[] input = Stream(
            PsV2,
            TexLdToken, TempDest(0, 0xF), TempSrc(1, 0x04), SamplerSrc(0),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        tokens[1].Should().Be(MovToken);
        tokens[2].Should().Be(TempDest(2, 0xF), because: "fresh temp above r0/r1");
        tokens[3].Should().Be(TempSrc(1, 0x04), because: "the mov keeps src0's original swizzle");
        tokens[4].Should().Be(TexLdToken);
        tokens[5].Should().Be(TempDest(0, 0xF));
        tokens[6].Should().Be(TempSrc(2, 0xE4), because: "texld's coord source is now the unswizzled temp");
        tokens[7].Should().Be(SamplerSrc(0));
    }

    [Fact]
    public void TexldSrc0Swizzle_AtSm3_IsPassthrough()
    {
        byte[] input = Stream(
            PsV3,
            TexLdToken, TempDest(0, 0xF), TempSrc(1, 0x04), SamplerSrc(0),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(input, because: "SM3 allows texld src0 swizzles — MojoShader only rejects them below SM3");
    }

    [Fact]
    public void TexldSrc0Modifier_AtSm3_IsRoutedThroughTemp()
    {
        // texld r0, -r1, s0 — identity swizzle but a negate source modifier (bits
        // [27:24] = 1). MojoShader's "TEXLD src0 must have no modifiers" applies to
        // every SM2+ major, so SM3 must patch this even though the swizzle is legal.
        uint negatedSrc = TempSrc(1, 0xE4) | 0x0100_0000u;
        byte[] input = Stream(
            PsV3,
            TexLdToken, TempDest(0, 0xF), negatedSrc, SamplerSrc(0),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        tokens.Should().HaveCount(9, because: "one compensating mov (3 tokens) is inserted");
        tokens[1].Should().Be(MovToken);
        tokens[2].Should().Be(TempDest(2, 0xF), because: "fresh temp above r0/r1");
        tokens[3].Should().Be(negatedSrc, because: "the mov keeps src0's swizzle AND modifier — the negate moves onto the mov");
        tokens[4].Should().Be(TexLdToken);
        tokens[5].Should().Be(TempDest(0, 0xF));
        tokens[6].Should().Be(TempSrc(2, 0xE4), because: "texld's coord source is now an unswizzled, unmodified temp");
        tokens[7].Should().Be(SamplerSrc(0));
        tokens[8].Should().Be(End);
    }

    [Fact]
    public void PredicatedPartialMaskTexkill_FailsWithSd0305()
    {
        // (p0) texkill r0(.x) — a would-be patch site that cannot take the fresh-temp
        // rewrite (the predicate guards the original instruction, not the inserted mov).
        byte[] input = Stream(
            PsV3,
            PredicatedTexKillToken, TempDest(0, 0x1), PredicateSrc,
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsFailure.Should().BeTrue(because: "the SD0305 contract says a predicated patch site fails loudly, never silently skipped");
        result.Error.Code.Should().Be("SD0305");
        result.Error.Message.Should().Contain("predicated");
    }

    [Fact]
    public void PredicatedFullMaskTexkill_IsPassthrough()
    {
        // (p0) texkill r0.xyzw — predicated but already canonical, so not a patch site.
        byte[] input = Stream(
            PsV3,
            PredicatedTexKillToken, TempDest(0, 0xF), PredicateSrc,
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(input, because: "a predicated instruction that needs no patching passes through untouched");
    }

    [Fact]
    public void TruncatedTrailingDef_WithPatchSite_FailsInsteadOfThrowing()
    {
        // The partial-mask texkill is a real patch site, forcing pass 2 to re-emit the
        // stream; the trailing def claims 5 operand tokens but the array ends after two.
        byte[] input = Stream(
            PsV3,
            TexKillToken, TempDest(0, 0x1),
            0x05000051, 0xA00F0000, 0x3F800000);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsFailure.Should().BeTrue(because: "a truncated stream must fail as a Result — never throw, never emit corrupt bytes");
        result.Error.Code.Should().Be("SD0305");
        result.Error.Message.Should().Contain("truncated or desynchronized");
    }

    [Fact]
    public void CommentLengthFieldOverrun_WithPatchSite_FailsInsteadOfThrowing()
    {
        // The comment after the patch site claims 0x7FFF payload dwords, but only the
        // end token follows — a lying length field must not drive an out-of-range copy.
        byte[] input = Stream(
            PsV3,
            TexKillToken, TempDest(0, 0x1),
            0x7FFFFFFE,
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsFailure.Should().BeTrue(because: "a comment length overrun must fail as a Result — never throw, never emit corrupt bytes");
        result.Error.Code.Should().Be("SD0305");
        result.Error.Message.Should().Contain("truncated or desynchronized");
    }

    [Fact]
    public void DefWithZeroOperandTokens_WithPatchSite_FailsInsteadOfThrowing()
    {
        // def whose operand-count field lies (zero) — re-emitting it would duplicate a
        // dword (8 bytes appended, 4 consumed) and desynchronize the rest of the stream.
        byte[] input = Stream(
            PsV3,
            0x00000051,
            TexKillToken, TempDest(0, 0x1),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsFailure.Should().BeTrue(because: "a def with no operands must fail as a Result — never duplicate bytes into the output");
        result.Error.Code.Should().Be("SD0305");
        result.Error.Message.Should().Contain("truncated or desynchronized");
    }

    [Fact]
    public void CommentBlocks_PassThroughByteIdentical()
    {
        // A 3-dword comment (stand-in for the CTAB) ahead of a patched texkill.
        byte[] input = Stream(
            PsV3,
            0x0003FFFE, 0x42415443, 0xDEADBEEF, 0x12345678,
            TexKillToken, TempDest(1, 0x1),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        tokens[1].Should().Be(0x0003FFFE);
        tokens[2].Should().Be(0x42415443u);
        tokens[3].Should().Be(0xDEADBEEFu);
        tokens[4].Should().Be(0x12345678u, because: "comment payloads (the CTAB) must never be altered");
    }

    [Fact]
    public void DefLiteralFloats_DoNotConfuseTheScan()
    {
        // def c0, <floats with bit 31 set that would misparse as temp-register params>.
        uint negFloat = BitConverter.SingleToUInt32Bits(-1.0f); // 0xBF800000 — bit 31 set
        byte[] input = Stream(
            PsV3,
            0x05000051, 0xA00F0000, negFloat, negFloat, negFloat, negFloat, // def c0
            TexKillToken, TempDest(0, 0x1),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        // Fresh temp must be r1 (only r0 in use) — if the def floats were parsed as
        // parameter tokens the temp index would be inflated or wrong.
        tokens[8].Should().Be(TempDest(1, 0xF));
    }

    [Fact]
    public void DefLiteralsAtOrAboveTwoPow32_AreClampedSignPreserving()
    {
        // vkd3d's discard sentinel −2³² (0xCF800000) misprints as −0.0 through
        // MojoShader's 32-bit unsigned-long float printer — the patcher clamps any
        // finite |f| ≥ 2³² def literal to the same-signed largest float below 2³².
        byte[] input = Stream(
            PsV3,
            0x05000051, 0xA00F0003, 0xCF800000, 0x4F800000, 0x4F7FFFFF, 0x7F800000, // def c3
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        uint[] tokens = Tokens(result.Value);
        tokens[3].Should().Be(0xCF7FFFFFu, because: "−2³² clamps to −4294967040.0, keeping the sign texkill tests");
        tokens[4].Should().Be(0x4F7FFFFFu, because: "+2³² clamps to +4294967040.0");
        tokens[5].Should().Be(0x4F7FFFFFu, because: "the largest float below 2³² is already printable — unchanged");
        tokens[6].Should().Be(0x7F800000u, because: "infinity is not in the misprint domain and stays untouched");
    }

    [Fact]
    public void DefiIntegerLiterals_AreNeverClamped()
    {
        // defi c0 with integer payloads that bit-pattern like huge floats.
        byte[] input = Stream(
            PsV3,
            0x05000030, 0xA00F0000, 0xCF800000, 0x4F800000, 0x00000007, 0x00000000, // defi
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(input, because: "defi carries integers, not floats — no clamping applies");
    }

    [Fact]
    public void NonD3d9Blob_IsPassthrough()
    {
        byte[] input = Stream(0x43425844 /* 'DXBC' */, 0, 0, 0);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(input);
    }

    [Fact]
    public void NoFreeTemp_FailsLoudly()
    {
        // ps_2_0 allows r0..r11; reference r11 so the patch temp would be r12 — over the limit.
        byte[] input = Stream(
            PsV2,
            MovToken, TempDest(11, 0xF), TempSrc(0, 0xE4),
            TexKillToken, TempDest(1, 0x1),
            End);

        var result = D3d9BytecodePatcher.PatchForMojoShader(input, "t.fx");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0305");
        result.Error.Message.Should().Contain("free temporary register");
    }
}
