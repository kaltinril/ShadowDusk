#nullable enable
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// <c>SD0227</c>: two legacy samplers one shader reads through ONE explicit register. fxc refuses
/// it, and so does <c>mgfxc</c> 3.8.4.1 on OpenGL and DirectX_11 (<c>X4500: overlapping register
/// semantics not yet implemented 's0'</c>, located at the second declaration; measured
/// 2026-10-03). The SM4 rewrite drops the register clause, so ShadowDusk used to compile it and
/// quietly give the second sampler the next unit. The accepted shapes are the ones <c>mgfxc</c>
/// was measured to accept on the same targets.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OverlappingLegacySamplerRegisterTests
{
    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string Technique = "\ntechnique Main { pass P0 { PixelShader = compile PS_SHADERMODEL MainPS(); } };\n";

    private static string Effect(string declarations, string body) =>
        Header + declarations + "\nfloat4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { " + body + " }" + Technique;

    public static TheoryData<PlatformTarget> Targets() => new()
    {
        PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.DirectX12, PlatformTarget.Vulkan,
    };

    public static TheoryData<string, string, int, string> RefusedShapes() => new()
    {
        { "sampler2D A : register(s0);\nsampler2D B : register(s0);", "return tex2D(A, uv) * tex2D(B, uv);", 0, "bare" },
        { "sampler A : register(s2);\nsampler B : register(s2);", "return tex2D(A, uv) * tex2D(B, uv);", 2, "untyped" },
        { "sampler2D A : register(s1) = sampler_state { MinFilter = Point; };\nsampler2D B : register(s1) = sampler_state { MinFilter = Linear; };",
          "return tex2D(A, uv) * tex2D(B, uv);", 1, "state blocks" },
        { "texture T;\nsampler2D A : register(s0) = sampler_state { Texture = <T>; };\nsampler2D B : register(s0) = sampler_state { Texture = <T>; };",
          "return tex2D(A, uv) * tex2D(B, uv);", 0, "one texture" },
    };

    public static IEnumerable<object[]> RefusedOnEveryTarget() =>
        from object[] shape in RefusedShapes()
        from object[] target in Targets()
        select shape.Append(target[0]).ToArray();

    [Theory]
    [MemberData(nameof(RefusedOnEveryTarget))]
    public async Task TwoReadLegacySamplersOnOneRegister_AreRefusedWithSD0227(
        string declarations, string body, int register, string shape, PlatformTarget target)
    {
        string source = Effect(declarations, body);
        var result = await Compile(source, target);

        result.IsFailure.ShouldBeTrue($"{shape} on {target}: mgfxc refuses this shape (X4500), so it must not compile");
        // Only the errors: a non-Windows DirectX 12 compile also carries its unsigned-DXIL warnings.
        ShaderError error = result.Error.Where(e => e.Severity == ShaderErrorSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0227");
        error.Message.ShouldContain("'A' and 'B'", Case.Sensitive);
        error.Message.ShouldContain($"register s{register}", Case.Sensitive);
        error.Message.ShouldContain("X4500", Case.Sensitive);

        // fxc reports the SECOND declaration; so does this.
        string[] lines = source.Split('\n');
        error.Line.ShouldBeGreaterThan(0);
        lines[error.Line - 1].ShouldContain(" B ", Case.Sensitive);
    }

    public static TheoryData<string, string, string> AcceptedShapes() => new()
    {
        // mgfxc compiles all of these on OpenGL and DirectX_11 (measured 2026-10-03).
        { "sampler2D A : register(s0);\nsampler2D B : register(s0);", "return tex2D(A, uv);", "second sampler never read" },
        { "sampler2D A : register(s0);\nsampler2D B : register(s1);", "return tex2D(A, uv) * tex2D(B, uv);", "distinct registers" },
        { "sampler2D A : register(s0);\nTexture2D T : register(t0);\nSamplerState S;", "return tex2D(A, uv) * T.Sample(S, uv);", "a texture register of the same number" },
        { "sampler2D A : register(s0);\nfloat4 C : register(c0);", "return tex2D(A, uv) * C;", "a constant register of the same number" },
    };

    [Theory]
    [MemberData(nameof(AcceptedShapes))]
    public async Task ShapesMgfxcAccepts_StillCompileOnDirectX(string declarations, string body, string shape)
    {
        var result = await Compile(Effect(declarations, body), PlatformTarget.DirectX);
        result.IsSuccess.ShouldBeTrue(result.IsFailure
            ? $"{shape}: " + string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
            : "");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task SamplerSharingItsRegisterWithAnotherResourceKind_StillCompiles(PlatformTarget target)
    {
        foreach ((string declarations, string body) in new[]
        {
            ("sampler2D A : register(s0);\nsampler2D B : register(s0);", "return tex2D(A, uv);"),
            ("sampler2D A : register(s0);\nTexture2D T : register(t0);\nSamplerState S;", "return tex2D(A, uv) * T.Sample(S, uv);"),
            ("sampler2D A : register(s0);\nfloat4 C : register(c0);", "return tex2D(A, uv) * C;"),
        })
        {
            var result = await Compile(Effect(declarations, body), target);
            result.IsSuccess.ShouldBeTrue(result.IsFailure
                ? $"{target}: " + string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "");
        }
    }

    [Fact]
    public async Task ClashInAnInactiveBranch_DoesNotCount_OnOpenGl()
    {
        // On OpenGL the #else branch (register s0) is dead; only the live s1 counts.
        string source = Effect(
            "sampler2D A : register(s0);\n#if OPENGL\nsampler2D B : register(s1);\n#else\nsampler2D B : register(s0);\n#endif",
            "return tex2D(A, uv) * tex2D(B, uv);");
        (await Compile(source, PlatformTarget.OpenGL)).IsSuccess.ShouldBeTrue();
        (await Compile(source, PlatformTarget.DirectX)).Error.Where(e => e.Severity == ShaderErrorSeverity.Error).ShouldHaveSingleItem().Code.ShouldBe("SD0227");
    }

    private static async Task<Result<CompiledShader, ShaderError[]>> Compile(string source, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return await new EffectCompiler().CompileAsync(
            source, new CompilerOptions { Target = target, SourceFileName = "overlap.fx" }, cts.Token);
    }
}
