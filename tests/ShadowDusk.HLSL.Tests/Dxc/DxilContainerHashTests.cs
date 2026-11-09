#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Issue #478: the managed DXIL container hash. The byte-for-byte oracle against the pinned
/// <c>dxil.dll</c> signer is <c>Compile_DxilHash_MatchesTheRetailHashOverTheFixtureCorpus</c>
/// (integration); this file pins the contract that needs no native: where the hash sits, when it
/// is written, and that it is the retail variant (a plain MD5 would differ).
/// </summary>
public sealed class DxilContainerHashTests
{
    private static byte[] Container(int length, bool signed = false)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'D'; bytes[1] = (byte)'X'; bytes[2] = (byte)'B'; bytes[3] = (byte)'C';
        for (int i = 20; i < length; i++)
            bytes[i] = (byte)(i * 31 + 7);
        if (signed)
            for (int i = 4; i < 20; i++)
                bytes[i] = 0xAB;
        return bytes;
    }

    [Fact]
    public void SignInPlace_ZeroHash_WritesTheComputedHash_WhichDiffersFromZero()
    {
        byte[] container = Container(300);
        DxilContainerHash.IsUnsigned(container).ShouldBeTrue();

        // Positive control: the zeroed hash must differ from the computed one.
        byte[] computed = new byte[16];
        DxilContainerHash.Compute(container.AsSpan(DxilContainerHash.PrefixLength), computed);
        computed.ShouldNotBe(new byte[16]);

        DxilContainerHash.SignInPlace(container).ShouldBeTrue();

        container.AsSpan(4, 16).ToArray().ShouldBe(computed);
        DxilContainerHash.IsUnsigned(container).ShouldBeFalse();
    }

    [Fact]
    public void SignInPlace_AlreadySigned_LeavesTheContainerUntouched()
    {
        byte[] container = Container(300, signed: true);
        byte[] before = (byte[])container.Clone();

        DxilContainerHash.SignInPlace(container).ShouldBeFalse();

        container.ShouldBe(before);
    }

    [Fact]
    public void SignInPlace_NotADxbcContainer_LeavesTheBytesUntouched()
    {
        byte[] bytes = Container(300);
        bytes[0] = (byte)'X';
        byte[] before = (byte[])bytes.Clone();

        DxilContainerHash.SignInPlace(bytes).ShouldBeFalse();
        DxilContainerHash.SignInPlace(new byte[10]).ShouldBeFalse();

        bytes.ShouldBe(before);
    }

    [Fact]
    public void SignInPlace_OnlyTheHashChanges()
    {
        byte[] container = Container(517);
        byte[] before = (byte[])container.Clone();

        DxilContainerHash.SignInPlace(container).ShouldBeTrue();

        container.AsSpan(0, 4).ToArray().ShouldBe(before.AsSpan(0, 4).ToArray());
        container.AsSpan(20).ToArray().ShouldBe(before.AsSpan(20).ToArray());
    }

    [Fact]
    public void Compute_IsNotPlainMd5()
    {
        // The retail variant moves the bit count to the first word of the last block and replaces
        // the trailing length with 1 | (byteCount << 1); a standard MD5 of the same bytes differs.
        byte[] data = Container(120).AsSpan(20).ToArray();
        byte[] retail = new byte[16];
        DxilContainerHash.Compute(data, retail);

        System.Security.Cryptography.MD5.HashData(data).ShouldNotBe(retail);
    }

    [Theory]
    // Every padding shape: empty, remainder < 56, exactly 55/56 (the one-/two-block boundary),
    // 63/64 (a full block), 119/120, and several full blocks.
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(1000)]
    public void Compute_EveryPaddingShape_IsDeterministicAndSensitiveToEachByte(int length)
    {
        byte[] data = Container(length + 20).AsSpan(20).ToArray();
        byte[] first = new byte[16];
        byte[] second = new byte[16];
        DxilContainerHash.Compute(data, first);
        DxilContainerHash.Compute(data, second);
        first.ShouldBe(second);

        if (length > 0)
        {
            data[length - 1] ^= 1;
            byte[] changed = new byte[16];
            DxilContainerHash.Compute(data, changed);
            changed.ShouldNotBe(first);
        }
    }

    [Fact]
    public void IsUnsignedNotice_MatchesOnlyDxcsOwnUnsignedWarning()
    {
        const string text = "DXIL signing library (dxil.dll,libdxil.so) not found.  Resulting DXIL will not be signed for use in release environments.";
        DxilContainerHash.IsUnsignedNotice(new ShaderError("a.fx", 0, 0, "X0000", text, ShaderErrorSeverity.Warning)).ShouldBeTrue();
        DxilContainerHash.IsUnsignedNotice(new ShaderError("a.fx", 0, 0, "X0000", "warning: " + text, ShaderErrorSeverity.Warning)).ShouldBeTrue();
        DxilContainerHash.IsUnsignedNotice(new ShaderError("a.fx", 0, 0, "X0000", "implicit truncation of vector type", ShaderErrorSeverity.Warning)).ShouldBeFalse();
    }

    private const string Shader = """
        float4 Tint;
        float4 PS(float4 p : SV_POSITION, float2 uv : TEXCOORD0) : SV_Target0 { return float4(uv, 0, 1) * Tint; }
        """;

    [Fact]
    public async Task ManagedHashEqualsDxilDll_AndTheWholeContainerMatchesAnUnvalidatedCompile()
    {
        // On Windows the validated build carries dxil.dll's hash; elsewhere both builds carry the
        // managed one. Either way the two containers must be identical and signed.
        using var compiler = new DxcShaderCompiler();
        DxcCompileRequest Request(bool skipValidation) => new()
        {
            HlslSource = Shader,
            SourceFileName = "oracle.hlsl",
            EntryPoint = "PS",
            Stage = ShaderStage.Pixel,
            Platform = PlatformTarget.DirectX12,
            Options = new DxcCompileOptions { SkipValidation = skipValidation },
        };

        var signed = await compiler.CompileAsync(Request(false));
        var unvalidated = await compiler.CompileAsync(Request(true));
        signed.IsSuccess.ShouldBeTrue();
        unvalidated.IsSuccess.ShouldBeTrue();

        // The validated build carries dxil.dll's hash; the -Vd build is signed by the managed hash.
        // Same container, byte for byte: only the hash could ever differ, and it does not.
        signed.Value.Bytes.Span.ToArray().ShouldBe(unvalidated.Value.Bytes.Span.ToArray());
        DxilContainerHash.IsUnsigned(signed.Value.Bytes.Span).ShouldBeFalse();
    }
}
