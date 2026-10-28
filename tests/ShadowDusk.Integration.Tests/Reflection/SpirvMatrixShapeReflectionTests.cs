#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Reflection;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// Issue #423: the pure-managed <see cref="SpirvReflector"/> (Vulkan, and the browser's OpenGL) must
/// report a matrix in its HLSL shape, as the native DXIL oracle does. DXC lowers an HLSL
/// <c>floatRxC</c> to a SPIR-V matrix of R column vectors of C components, and the reflector used to
/// read that back as C x R, so every non-square matrix on Vulkan was reported transposed: its MGFX
/// header disagreed with the declaration (MonoGame's runtime sizes the upload by it) and its
/// initializer default was dropped with <c>SD0017</c>. Real mgfxc 3.8.5 <c>/Profile:Vulkan</c> writes
/// the same transposed header (measured 2026-10-28), so this is a deliberate departure from it.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "Vulkan")]
public sealed class SpirvMatrixShapeReflectionTests
{
    public static TheoryData<string, string> Shapes => new()
    {
        { "float2x3", "float2x3 M; float4 Pad;" },
        { "float3x2", "float3x2 M; float4 Pad;" },
        { "float2x4", "float2x4 M; float4 Pad;" },
        { "float4x2", "float4x2 M; float4 Pad;" },
        { "float3x4", "float3x4 M; float4 Pad;" },
        { "float4x3", "float4x3 M; float4 Pad;" },
        { "float4x3[2]", "float4x3 M[2]; float4 Pad;" },
        { "row_major float2x3", "row_major float2x3 M; float4 Pad;" },
        { "float2x2", "float2x2 M; float4 Pad;" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task SpirvReflection_ReportsTheHlslShape_LikeTheDxilOracle(string label, string declarations)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        string hlsl = declarations + "\nfloat4 MainPS(float4 pos : SV_Position) : SV_Target0 { return Pad + " +
                      (label.EndsWith("]", StringComparison.Ordinal) ? "M[1][0].x" : "M[0][0]") + "; }\n";

        ReflectedEffect spirv = Reflect(new SpirvReflector().Reflect(
            await CompileAsync(hlsl, PlatformTarget.Vulkan, cts.Token)));
        ReflectedEffect dxil = Reflect(new DxilReflectionExtractor().Extract(
            await CompileAsync(hlsl, PlatformTarget.DirectX12, cts.Token), cts.Token));

        VariableReflection fromSpirv = spirv.ConstantBuffers.Single().Variables.Single(v => v.Name == "M");
        VariableReflection fromDxil = dxil.ConstantBuffers.Single().Variables.Single(v => v.Name == "M");

        fromDxil.ParameterClass.ShouldBe(EffectParameterClass.Matrix, label);
        (fromSpirv.Rows, fromSpirv.Columns).ShouldBe((fromDxil.Rows, fromDxil.Columns), label);
        fromSpirv.Elements.ShouldBe(fromDxil.Elements, label);
        fromSpirv.StartOffset.ShouldBe(fromDxil.StartOffset, label);

        // And the HLSL shape it names, not just "whatever DXIL said".
        string shape = label.Replace("row_major ", "", StringComparison.Ordinal).Replace("[2]", "", StringComparison.Ordinal);
        (fromSpirv.Rows, fromSpirv.Columns).ShouldBe((shape[5] - '0', shape[7] - '0'), label);
    }

    public static TheoryData<string> TrailingMatrices => new()
    {
        "float2 Offset; float2x2 M;",            // C_Vec2Mat2's shape: 16 + 24 bytes, a 48-byte buffer
        "float2x2 M;",
        "float2x3 M;",
        "float2x4 M;",
        "float3x2 M;",
        "float3x3 M;",
        "float4x3 M;",
        "row_major float2x2 M;",
        "row_major float3x2 M;",
        "float4 V; float2x3 M[2];",
    };

    /// <summary>
    /// A buffer that ENDS in a matrix whose registers are not full used to be sized by the packed
    /// <c>4 * rows * cols</c> bytes (16 for a <c>float2x2</c>, which spans 24), a register short, so
    /// MonoGame DesktopVK wrote the matrix's last register past the end of the buffer and threw on the
    /// first draw. The size must be the DXIL oracle's.
    /// </summary>
    [Theory]
    [MemberData(nameof(TrailingMatrices))]
    public async Task SpirvReflection_SizesABufferEndingInAMatrix_LikeTheDxilOracle(string declarations)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        string read = declarations.Contains("M[2]", StringComparison.Ordinal) ? "M[1][0].x + V" : "M[0][0]";
        string hlsl = declarations + "\nfloat4 MainPS(float4 pos : SV_Position) : SV_Target0 { return " + read + "; }\n";

        ReflectedEffect spirv = Reflect(new SpirvReflector().Reflect(
            await CompileAsync(hlsl, PlatformTarget.Vulkan, cts.Token)));
        ReflectedEffect dxil = Reflect(new DxilReflectionExtractor().Extract(
            await CompileAsync(hlsl, PlatformTarget.DirectX12, cts.Token), cts.Token));

        spirv.ConstantBuffers.Single().SizeBytes.ShouldBe(dxil.ConstantBuffers.Single().SizeBytes, declarations);
    }

    private static ReflectedEffect Reflect(Result<ReflectedEffect, ShaderError> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "ok");
        return result.Value;
    }

    private static async Task<ReadOnlyMemory<byte>> CompileAsync(string hlsl, PlatformTarget platform, CancellationToken ct)
    {
        using var compiler = new DxcShaderCompiler();
        var request = new DxcCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "MatrixShape.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
            Platform       = platform,
            Options        = new DxcCompileOptions { AllowWarnings = true },
        };
        var result = await compiler.CompileAsync(request, ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "compilation must succeed");
        return result.Value.Bytes;
    }
}
