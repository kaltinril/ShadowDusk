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
/// <c>SD0017</c>) on harmless <c>#ifdef _DEBUG</c> blocks. The names only DXC defines
/// (<c>__GNUC__</c>, <c>__llvm__</c>, ...) are undefined to this view too, on every target, as to
/// <c>mgfxc</c>'s preprocessor and 0.22.1: only the Vulkan register pairing, which must know every
/// line DXC may compile, treats them as unknown.
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
    [MemberData(nameof(Targets))]
    public async Task AConditionalOnAMacroOnlyDxcDefines_ReadsAsUndefined_OnEveryTarget(PlatformTarget target, EffectContainer container)
    {
        // DXC defines __GNUC__ (measured with -P), mgfxc's preprocessor does not. The technique
        // list, legacy-sampler recovery and initializer defaults are mgfxc's, so they read it as
        // undefined on every target and keep 0.22.1's output: no SD0009.
        bool sm3 = target == PlatformTarget.Fna;
        string source = "#ifdef __GNUC__\n#define K 2\n#else\n#define K 1\n#endif\n" + MainEffect(sm3);

        CompiledShader shader = await CompileOk(source, target, container);

        if (container == EffectContainer.Mgfx)
            TechniqueNames(shader, target).ShouldBe(["Main"]);
        shader.Warnings.ShouldNotContain(w => w.Code == "SD0009");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ATechniqueUnderIfdefGnuc_IsDropped_AsMgfxcDropsIt(PlatformTarget target, EffectContainer container)
    {
        // The review of 2026-10-29 (gnuc_tech.fx): reading __GNUC__ as unknown on the DXC targets
        // changed the technique list from 0.22.1's (and mgfxc's) and added SD0009.
        bool sm3 = target == PlatformTarget.Fna;
        string source =
            $"float4 P(float4 c : COLOR0) : {Sem(sm3)} {{ return c * 0.5; }}\n#ifdef __GNUC__\n" +
            $"technique A {{ pass P0 {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n#else\n" +
            $"technique B {{ pass P0 {{ PixelShader = compile {Ps(sm3)} P(); }} }}\n#endif\n";

        CompiledShader shader = await CompileOk(source, target, container);

        if (container == EffectContainer.Mgfx)
            TechniqueNames(shader, target).ShouldBe(["B"]);
        NoViewWarnings(shader);
    }
}
