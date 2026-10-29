#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Tests.Fx2;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Conditionals on names that look like the compiler's own (<c>_XBOX</c>, <c>_DEBUG</c>,
/// <c>__PSSL__</c>, <c>_PS4</c>) but that neither DXC nor fxc defines. The preprocessed view
/// (techniques, legacy-sampler recovery, initializer defaults) reads them as undefined on every
/// target, as 0.22.1 and <c>mgfxc</c> do: the final review of 2026-10-29 measured that treating
/// every such name as "compiler-defined, value unknown" failed real effects on every target
/// ("missing entry point definition", <c>SD0010</c>, <c>SD0016</c>) and warned (<c>SD0009</c>,
/// <c>SD0017</c>) on harmless <c>#ifdef _DEBUG</c> blocks. And the names only DXC defines
/// (<c>__GNUC__</c>, <c>__llvm__</c>, ...) are unknown only where DXC compiles the shaders: the
/// DirectX 11 and FNA compilers never run DXC, so there they are undefined, as in 0.22.1.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReservedNameConditionalTests
{
    private static string Ps(bool sm3) => sm3 ? "ps_3_0" : "ps_4_0";

    private static string Sem(bool sm3) => sm3 ? "COLOR0" : "SV_TARGET";

    private static string MainEffect(bool sm3) =>
        $"float Intensity = 0.5;\nfloat4 P(float4 c : COLOR0) : {Sem(sm3)} {{ return c * Intensity; }}\n" +
        $"technique Main {{ pass {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n";

    private static string Guarded(string condition, bool sm3) =>
        $"{condition}\nfloat4 PX(float4 c : COLOR0) : {Sem(sm3)} {{ return XboxOnlyThing(c); }}\n" +
        $"technique Console {{ pass {{ PixelShader = compile {Ps(sm3)} PX(); }} }}\n#endif\n" + MainEffect(sm3);

    private static string Debug(bool sm3) =>
        $"float Intensity = 0.5;\nfloat4 P(float4 c : COLOR0) : {Sem(sm3)}\n{{\n    float4 r = c * Intensity;\n#ifdef _DEBUG\n    r.r = 1;\n#endif\n    return r;\n}}\n" +
        $"technique Main {{ pass {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n";

    private static string DebugWithIncludedTechnique(bool sm3) =>
        $"float4 P(float4 c : COLOR0) : {Sem(sm3)}\n{{\n#if defined(_DEBUG)\n    return float4(1, 0, 1, 1);\n#else\n    return c;\n#endif\n}}\n#include \"tech.fxh\"\n";

    private static async Task<CompiledShader> CompileOk(
        string source, PlatformTarget target, EffectContainer container = EffectContainer.Mgfx)
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        bool sm3 = target == PlatformTarget.Fna;
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            Container = container,
            SourceFileName = "Reserved.fx",
            IncludeResolver = new InMemoryIncludeResolver(new Dictionary<string, string>
            {
                ["tech.fxh"] = $"technique FromInclude {{ pass {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n",
            }),
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");
        return result.Value;
    }

    private static string[] TechniqueNames(CompiledShader shader, PlatformTarget target) =>
        target == PlatformTarget.Fna
            ? Fx2BinaryValidator.Parse(shader.Data).Techniques.Select(t => t.Name).ToArray()
            : MgfxBlobReader.Parse(shader.Data).Techniques.Select(t => t.Name).ToArray();

    public static TheoryData<PlatformTarget, EffectContainer> Targets() => new()
    {
        { PlatformTarget.OpenGL, EffectContainer.Mgfx },
        { PlatformTarget.OpenGL, EffectContainer.Knifx },
        { PlatformTarget.DirectX, EffectContainer.Mgfx },
        { PlatformTarget.DirectX12, EffectContainer.Mgfx },
        { PlatformTarget.Vulkan, EffectContainer.Mgfx },
        { PlatformTarget.Fna, EffectContainer.Mgfx },
    };

    private static void NoViewWarnings(CompiledShader shader) =>
        shader.Warnings.ShouldNotContain(w => w.Code == "SD0009" || w.Code == "SD0016" || w.Code == "SD0017",
            string.Join("; ", shader.Warnings.Select(w => $"{w.Code}: {w.Message}")));

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ATechniqueUnderIfdefXbox_IsDropped_AndItsShaderNeverCompiled(PlatformTarget target, EffectContainer container)
    {
        CompiledShader shader = await CompileOk(Guarded("#ifdef _XBOX", target == PlatformTarget.Fna), target, container);

        if (container == EffectContainer.Mgfx)
            TechniqueNames(shader, target).ShouldBe(["Main"]);
        NoViewWarnings(shader);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ATechniqueUnderAConsoleCompilerTest_IsDropped(PlatformTarget target, EffectContainer container)
    {
        CompiledShader shader = await CompileOk(
            Guarded("#if defined(__PSSL__) || defined(_PS4)", target == PlatformTarget.Fna), target, container);

        if (container == EffectContainer.Mgfx)
            TechniqueNames(shader, target).ShouldBe(["Main"]);
        NoViewWarnings(shader);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AHarmlessIfdefDebug_RaisesNoViewWarning(PlatformTarget target, EffectContainer container)
    {
        NoViewWarnings(await CompileOk(Debug(target == PlatformTarget.Fna), target, container));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ATechniqueFromAnInclude_IsFound_BesideAnIfDefinedDebug(PlatformTarget target, EffectContainer container)
    {
        CompiledShader shader = await CompileOk(DebugWithIncludedTechnique(target == PlatformTarget.Fna), target, container);

        if (container == EffectContainer.Mgfx)
            TechniqueNames(shader, target).ShouldBe(["FromInclude"]);
        NoViewWarnings(shader);
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task LegacySamplerSyntaxFromAMacro_BesideAnIfdefXbox_IsRecovered(PlatformTarget target)
    {
        const string Source =
            "#define DECLARE_TEX(Name, idx) sampler2D Name : register(s##idx)\n" +
            "#define SAMPLE(Name, uv) tex2D(Name, uv)\n" +
            "DECLARE_TEX(Tex, 0);\n" +
            "float4 PS(float2 uv : TEXCOORD0) : COLOR0\n{\n#ifdef _XBOX\n    return 0;\n#else\n    return SAMPLE(Tex, uv);\n#endif\n}\n" +
            "technique Main { pass P0 { PixelShader = compile ps_3_0 PS(); } }\n";

        NoViewWarnings(await CompileOk(Source, target));
    }

    [Theory]
    [InlineData(PlatformTarget.OpenGL, true)]
    [InlineData(PlatformTarget.Vulkan, true)]
    [InlineData(PlatformTarget.DirectX12, true)]
    [InlineData(PlatformTarget.DirectX, false)]
    [InlineData(PlatformTarget.Fna, false)]
    public async Task AConditionalOnAMacroOnlyDxcDefines_IsUnknownOnlyWhereDxcCompiles(PlatformTarget target, bool dxc)
    {
        // DXC defines __GNUC__ (measured with -P), so the view cannot know which branch DXC takes
        // and the technique list falls back to the raw source (SD0009). DirectX 11 and FNA never
        // run DXC: there __GNUC__ is undefined, as to mgfxc's fxc, and 0.22.1's output stays.
        bool sm3 = target == PlatformTarget.Fna;
        string source = "#ifdef __GNUC__\n#define K 2\n#else\n#define K 1\n#endif\n" + MainEffect(sm3);

        CompiledShader shader = await CompileOk(source, target);

        TechniqueNames(shader, target).ShouldBe(["Main"]);
        if (dxc)
            shader.Warnings.ShouldContain(w => w.Code == "SD0009" && w.Message.Contains("'__GNUC__'", StringComparison.Ordinal));
        else
            shader.Warnings.ShouldNotContain(w => w.Code == "SD0009");
    }
}
