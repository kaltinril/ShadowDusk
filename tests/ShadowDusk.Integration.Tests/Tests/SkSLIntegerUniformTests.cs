#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ShaderToy;
using ShadowDusk.SkSL;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #507: <c>uniform int</c> / <c>uniform bool</c> in SkSL input failed SD0210 on OpenGL. The
/// converter lowers them to float uniforms, so the converted effect compiles on every target.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SkSLIntegerUniformTests
{
    private const string Sksl = """
        uniform shader SpriteTexture;
        uniform int steps;
        uniform bool flip;
        half4 main(float2 c) { return flip ? SpriteTexture.eval(c) * half(steps) : half4(0); }
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task IntAndBoolUniforms_CompileOnTheTarget_AsFloatParameters(PlatformTarget target)
    {
        ConvertResult converted = SkSLToFxConverter.Convert(Sksl);
        converted.Success.ShouldBeTrue(string.Join("; ", converted.Diagnostics.Select(d => d.Message)));

        var result = await new EffectCompiler().CompileAsync(converted.Fx!, new CompilerOptions
        {
            Target = target,
            SourceFileName = "int-uniforms.fx",
        });

        result.IsFailure.ShouldBeFalse(result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
    }
}
