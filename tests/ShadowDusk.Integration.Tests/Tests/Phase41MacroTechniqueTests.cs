#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Core.Tests.Fx2;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 41 — macro-declared techniques. The MonoGame stock effects declare their
/// techniques ONLY through the <c>TECHNIQUE(name, vs, ps)</c> macro from
/// <c>Macros.fxh</c>; the raw FX pre-parse (which runs before macro expansion and ignores
/// macro-call forms) sees zero techniques, so the pipeline used to fail SD0010 before any
/// backend ran. The gated zero-technique fallback now DXC-preprocesses (`-P`) the source
/// with the target's platform macros and re-parses the expanded text, recovering the
/// literal <c>technique { ... }</c> blocks.
///
/// <para><b>DirectX is the proven win</b> (SM4 macro branch -> modern Texture2D -> vkd3d).
/// The OpenGL macro set deliberately lacks SM4/SM6, so the stock effects expand to their
/// legacy DX9/SM2 branch which ShadowDusk's modern DXC -> SPIR-V GL backend cannot compile;
/// that target is gated OUT of the recovery and keeps the honest SD0010 (documented GL
/// macro-model gap). See <see cref="OpenGl_MacroTechniqueEffect_KeepsLoudSd0010_NoCrash"/>.</para>
///
/// <para><b>FNA extension (GAP-1 closed on the FNA path).</b> The FNA path (<c>RunFna</c>)
/// now applies the same zero-technique macro recovery, with NO modern-branch gate: FNA's
/// vkd3d SM1-3 backend compiles the legacy (vs_2_0/ps_2_0) macro branch directly and never
/// uses DXC for codegen, so the GL legacy-branch SPIR-V crash cannot occur. The re-parse runs
/// in PreserveSm3 mode. Result: the stock effects that fit SM2 (SpriteEffect, AlphaTestEffect,
/// DualTextureEffect, Penumbra*) now compile on FNA; the ones that overflow the SM2 register
/// file (BasicEffect/SkinnedEffect, SD0305) or use a sub-SM2 profile (Gum's FnaSample uses
/// vs_1_1, SD0300) now fail for their HONEST downstream reason rather than the SD0010
/// technique-blindness. See <see cref="Fna_StockMacroEffects_ThatFitSm2_NowCompile"/> and the
/// two honest-limit pins below.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Phase41MacroTechniqueTests
{
    // The TECHNIQUE() declaration order in BasicEffect.fx (must match exactly — MonoGame
    // indexes techniques by declaration order; BasicEffect.cs relies on it).
    private static readonly string[] s_basicEffectTechniqueOrder =
    {
        "BasicEffect",
        "BasicEffect_NoFog",
        "BasicEffect_VertexColor",
        "BasicEffect_VertexColor_NoFog",
        "BasicEffect_Texture",
        "BasicEffect_Texture_NoFog",
        "BasicEffect_Texture_VertexColor",
        "BasicEffect_Texture_VertexColor_NoFog",
        "BasicEffect_VertexLighting",
        "BasicEffect_VertexLighting_NoFog",
        "BasicEffect_VertexLighting_VertexColor",
        "BasicEffect_VertexLighting_VertexColor_NoFog",
        "BasicEffect_VertexLighting_Texture",
        "BasicEffect_VertexLighting_Texture_NoFog",
        "BasicEffect_VertexLighting_Texture_VertexColor",
        "BasicEffect_VertexLighting_Texture_VertexColor_NoFog",
        "BasicEffect_OneLight",
        "BasicEffect_OneLight_NoFog",
        "BasicEffect_OneLight_VertexColor",
        "BasicEffect_OneLight_VertexColor_NoFog",
        "BasicEffect_OneLight_Texture",
        "BasicEffect_OneLight_Texture_NoFog",
        "BasicEffect_OneLight_Texture_VertexColor",
        "BasicEffect_OneLight_Texture_VertexColor_NoFog",
        "BasicEffect_PixelLighting",
        "BasicEffect_PixelLighting_NoFog",
        "BasicEffect_PixelLighting_VertexColor",
        "BasicEffect_PixelLighting_VertexColor_NoFog",
        "BasicEffect_PixelLighting_Texture",
        "BasicEffect_PixelLighting_Texture_NoFog",
        "BasicEffect_PixelLighting_Texture_VertexColor",
        "BasicEffect_PixelLighting_Texture_VertexColor_NoFog",
    };

    [Fact]
    public async Task DirectX_BasicEffect_MacroTechniques_CompileWithCorrectCountAndOrder()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        var (mgfx, error) = await CompileAsync("BasicEffect.fx", PlatformTarget.DirectX, ct);

        // No SD0010: the macro-declared techniques are recovered for the DX (SM4) target.
        error.ShouldBeNull(
            "BasicEffect's TECHNIQUE(...) macro techniques must be recovered on DirectX, not SD0010");
        mgfx.ShouldNotBeNull();

        MgfxBlobReader subject = MgfxBlobReader.Parse(mgfx!);

        subject.TechniqueCount.ShouldBe(32, customMessage: "BasicEffect.fx declares 32 TECHNIQUE() blocks");
        subject.Techniques.Select(t => t.Name).ShouldBe(
            s_basicEffectTechniqueOrder, customMessage: "techniques must appear in BasicEffect.fx declaration order (technique[0]=BasicEffect, [1]=BasicEffect_NoFog, ...)");
    }

    [Fact]
    public async Task DirectX_BasicEffect_StructurallyMatchesGoldenOrKnownDivergence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        var (mgfx, error) = await CompileAsync("BasicEffect.fx", PlatformTarget.DirectX, ct);
        error.ShouldBeNull();

        string goldenPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", "DirectX_11", "BasicEffect.mgfx");
        File.Exists(goldenPath).ShouldBeTrue($"golden expected at {goldenPath}");

        MgfxBlobReader subject = MgfxBlobReader.Parse(mgfx!);
        MgfxBlobReader golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));

        // Technique shape must match the golden exactly (count, names, order, pass counts).
        subject.TechniqueCount.ShouldBe(golden.TechniqueCount);
        subject.Techniques.Select(t => t.Name).ShouldBe(golden.Techniques.Select(t => t.Name));
        for (int t = 0; t < golden.TechniqueCount; t++)
            subject.Techniques[t].PassCount.ShouldBe(golden.Techniques[t].PassCount, customMessage: $"technique '{golden.Techniques[t].Name}' pass count must match the golden");

        // Constant-buffer SIZE must match the golden on DX (the runtime SetValue layout).
        var goldCbBySize = golden.ConstantBuffers.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var sub in subject.ConstantBuffers)
        {
            if (goldCbBySize.TryGetValue(sub.Name, out var gold))
                sub.Size.ShouldBe(gold.Size, customMessage: $"cbuffer '{sub.Name}' size must match the golden on DirectX");
        }

        // Every golden value-class parameter must be reachable by name with matching shape.
        // (Object-class sampler/texture params carry the two pinned, render-proven shapes
        // already tolerated across the corpus — not asserted strictly here.)
        const byte ClassObject = 3;
        var subjByName = subject.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var gold in golden.Parameters.Where(p => p.Class != ClassObject))
        {
            subjByName.ContainsKey(gold.Name).ShouldBeTrue(customMessage: $"golden value-class parameter '{gold.Name}' must be reachable by name");
            var sub = subjByName[gold.Name];
            (sub.Class, sub.Type, sub.Rows, sub.Columns).ShouldBe(
                (gold.Class, gold.Type, gold.Rows, gold.Columns), customMessage: $"parameter '{gold.Name}' value-class shape must match the golden");
        }
    }

    [Fact]
    public async Task OpenGl_MacroTechniqueEffect_KeepsLoudSd0010_NoCrash()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        // On OpenGL the stock effect expands to its legacy DX9/SM2 branch, which the modern
        // DXC -> SPIR-V GL backend cannot compile (it would crash DXC's native codegen). The
        // recovery is gated OUT for GL, so the effect returns a clean, loud SD0010 instead of
        // crashing the process. This documents the GL macro-model gap (Phase 41 follow-up).
        var (mgfx, error) = await CompileAsync("BasicEffect.fx", PlatformTarget.OpenGL, ct);

        mgfx.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.Code.ShouldBe("SD0010", customMessage: "the OpenGL macro-model gap surfaces as a loud SD0010, never a native crash");
    }

    [Fact]
    public async Task TechniqueFreeEffect_StillReturnsSd0010()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = cts.Token;

        // A genuinely technique-free source: valid HLSL, no technique block and no
        // TECHNIQUE(...) macro. The recovery's DXC -P expansion finds no techniques either,
        // so SD0010 is the correct, unchanged result.
        const string source = """
            float4 PSMain() : SV_Target0 { return float4(1, 0, 0, 1); }
            """;

        var options = new CompilerOptions
        {
            Target          = PlatformTarget.DirectX,
            SourceFileName  = "TechniqueFree.fx",
            IncludeResolver = new FileSystemIncludeResolver(),
        };

        var result = await new EffectCompiler().CompileAsync(source, options, ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SD0010").ShouldHaveSingleItem();
    }

    // -----------------------------------------------------------------------
    // FNA path — the GAP-1 fix (zero-technique macro recovery extended to RunFna).
    // -----------------------------------------------------------------------

    /// <summary>
    /// The stock macro-technique effects that FIT the SM2 register file now compile on FNA
    /// (they were SD0010 before the FNA fallback). Proves the macro recovery runs on the FNA
    /// path and produces a well-formed fx_2_0 binary with techniques + SM&lt;=3 shaders.
    /// </summary>
    public static TheoryData<string> FnaCompilableStockEffects() => new()
    {
        "SpriteEffect.fx",
        "AlphaTestEffect.fx",
        "DualTextureEffect.fx",
        "PenumbraHull.fx",
        "PenumbraLight.fx",
        "PenumbraShadow.fx",
        "PenumbraTexture.fx",
    };

    [Theory]
    [Trait("Platform", "FNA")]
    [MemberData(nameof(FnaCompilableStockEffects))]
    public async Task Fna_StockMacroEffects_ThatFitSm2_NowCompile(string fixtureFileName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        var (data, error) = await CompileAsync(fixtureFileName, PlatformTarget.Fna, ct);

        error.ShouldBeNull(
            $"'{fixtureFileName}' declares its techniques via the TECHNIQUE() macro; the FNA " +
            "zero-technique recovery must detect them (not SD0010) and compile to fx_2_0");
        data.ShouldNotBeNull();

        // The output must be a real fx_2_0 binary MojoShader can parse, with techniques and
        // SM<=3 shader blobs (the FNA ceiling).
        Fx2ParsedEffect effect = Fx2BinaryValidator.Parse(data!);
        effect.Techniques.ShouldNotBeEmpty($"'{fixtureFileName}' declares macro techniques");
        effect.Shaders.ShouldNotBeEmpty($"'{fixtureFileName}' declares at least one compiled pass");
        foreach (Fx2ParsedShader shader in effect.Shaders)
            (shader.VersionToken & 0xFFFF).ShouldBeLessThanOrEqualTo(0x0300u, customMessage: $"shader version 0x{shader.VersionToken:X8} in '{fixtureFileName}' must be SM <= 3");
    }

    [Fact]
    [Trait("Platform", "FNA")]
    public async Task Fna_BasicEffect_MacroRecovered_ThenLoudSm2RegisterLimit_NotSd0010()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        // The FNA recovery DETECTS BasicEffect's macro techniques (GAP-1 fixed), so it no
        // longer returns SD0010. BasicEffect's SM2 (vs_2_0/ps_2_0) expansion then runs out of
        // SM2 temp registers during the MojoShader texkill/texld canonicalization patch (the
        // patcher needs one more temp than the SM2 12-temp limit allows), which surfaces as the
        // honest, loud SD0305 (a real shader-model limit, documented Phase 40) - NOT the
        // technique-blindness.
        var (data, error) = await CompileAsync("BasicEffect.fx", PlatformTarget.Fna, ct);

        data.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.Code.ShouldBe("SD0305", customMessage: "GAP-1 is fixed on FNA (techniques recovered); BasicEffect then fails on the honest " +
            "SM2 register-pressure limit (SD0305), not SD0010");
    }

    [Fact]
    [Trait("Platform", "FNA")]
    public async Task Fna_GumFnaSample_MacroRecovered_ThenRejectsVs11_Sd0300_NotSd0010()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        // Gum's FNA sample declares its technique via its own #define TECHNIQUE macro
        // (VertexShader = compile vs_1_1 ...). The FNA recovery now detects it (GAP-1 fixed),
        // so it reaches profile validation and is declined for using vs_1_1 (SM1) - below
        // ShadowDusk's FNA SM2 floor - with the actionable SD0300, NOT SD0010.
        //
        // NOTE (current limit, not fxc-equivalent): real fxc /T fx_2_0 + MojoShader DO compile
        // vs_1_1. ShadowDusk's SD0300 SM2 floor is a deliberate conservatism (vkd3d 1.17's SM1
        // backend has known gaps and the SM1 output path is unvalidated against real FNA, Phase
        // 40). Revisit this pin if/when the vkd3d SM1 path is validated; until then SD0300 with
        // actionable guidance (use vs_2_0) is the honest, intended behavior.
        var (data, error) = await CompileAsync(
            "third-party/Gum/FnaSample-Shader.fx", PlatformTarget.Fna, ct);

        data.ShouldBeNull();
        error.ShouldNotBeNull();
        error!.Code.ShouldBe("SD0300", customMessage: "GAP-1 is fixed on FNA (the macro technique is recovered); the shader is then " +
            "declined for its sub-SM2 vs_1_1 profile (SD0300), not SD0010");
    }

    // -----------------------------------------------------------------------

    private static async Task<(byte[]? Mgfx, ShaderError? Error)> CompileAsync(
        string fixtureFileName, PlatformTarget target, CancellationToken ct)
    {
        string fxPath = TestHelpers.FixturePath(fixtureFileName);
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var options = new CompilerOptions
        {
            Target          = target,
            SourceFileName  = fxPath,
            IncludeResolver = new FileSystemIncludeResolver(),
        };

        var result = await new EffectCompiler().CompileAsync(source, options, ct);
        if (result.IsSuccess)
            return (result.Value.Data, null);

        return (null, result.Error.FirstOrDefault()
            ?? new ShaderError(fxPath, 0, 0, "SD9999", "compile failed with no diagnostic"));
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
