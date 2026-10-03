#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.ImageTests.GlContext;
using ShadowDusk.ImageTests.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Issue #215 rewriter Rule 16 regression: the <c>sd_reduce_angle</c> range-reduction
/// helpers must be declared before EVERY function that calls them, not just before
/// <c>main</c>. A <c>[noinline]</c> HLSL function survives DXC + SPIRV-Cross as its own
/// GLSL function emitted before <c>main</c>; the first Rule 16 cut anchored the helpers
/// at <c>main</c>, so that function called an undeclared function and the shader failed
/// to compile in GLSL 1.10 / ES 1.00 (glslangValidator: "'sd_reduce_angle' : no matching
/// overloaded function found"). This compiles the real HLSL through the full OpenGL
/// pipeline, asserts the declaration order, and compiles + links the emitted fragment
/// shader in the real GL driver.
/// </summary>
[Trait("Category", "ImageRegression")]
[Trait("Platform", "OpenGL")]
[Collection(GlContextCollection.Name)]
public sealed class Issue215TrigReductionHelperOrderTests
{
    private const string NoinlineSource = """
float angle;

[noinline]
float helper(float x)
{
    return sin(x) + cos(x * 2.0);
}

struct VSOut { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };

float4 PS(VSOut input) : COLOR
{
    float v = helper(input.TexCoord.x * 800.0 + angle);
    return float4(v, v, v, 1.0);
}

technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
""";

    private readonly GlContextFixture _fixture;
    private readonly ITestOutputHelper _output;

    public Issue215TrigReductionHelperOrderTests(GlContextFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private static async Task<string> CompileFragmentAsync(CancellationToken ct)
    {
        var result = await new EffectCompiler().CompileAsync(NoinlineSource, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue215Noinline.fx",
        }, ct);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "compile ok");
        return GlslShaderExtractor.Extract(result.Value.Data).FragmentSource;
    }

    [Fact]
    public async Task NoinlineFunctionCallingSin_HelperIsDeclaredBeforeIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string ps = await CompileFragmentAsync(cts.Token);
        _output.WriteLine(ps);

        int helperDecl = ps.IndexOf("float sd_reduce_angle(float x)", StringComparison.Ordinal);
        int userFn = ps.IndexOf("float helper(", StringComparison.Ordinal);
        int main = ps.IndexOf("void main()", StringComparison.Ordinal);
        helperDecl.ShouldBeGreaterThan(0, "the Rule 16 helper must be emitted");
        userFn.ShouldBeGreaterThan(helperDecl,
            "DXC + SPIRV-Cross must keep the [noinline] function separate, and the helper must precede it");
        main.ShouldBeGreaterThan(userFn);
        ps.ShouldContain("sin(sd_reduce_angle(x))", Case.Sensitive);
    }

    [Fact]
    public async Task NoinlineFunctionCallingSin_CompilesAndLinks_InRealDriver()
    {
        if (_fixture.IsSkipped) { _output.WriteLine(_fixture.SoftSkipLine); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string ps = await CompileFragmentAsync(cts.Token);

        using (_fixture.MakeContextCurrent())
        {
            // Throws GlslCompileException (with the driver's info log) on a compile or
            // link failure, e.g. a call to a not-yet-declared function.
            using var program = GlslShaderProgram.Compile(
                _fixture.Gl, PassthroughVertexShader.PickFor(ps), ps);
            program.Handle.ShouldNotBe(0u);
        }
    }
}
