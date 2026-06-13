#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
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
        error.Should().BeNull(
            "BasicEffect's TECHNIQUE(...) macro techniques must be recovered on DirectX, not SD0010");
        mgfx.Should().NotBeNull();

        MgfxBlobReader subject = MgfxBlobReader.Parse(mgfx!);

        subject.TechniqueCount.Should().Be(32, "BasicEffect.fx declares 32 TECHNIQUE() blocks");
        subject.Techniques.Select(t => t.Name).Should().Equal(
            s_basicEffectTechniqueOrder,
            "techniques must appear in BasicEffect.fx declaration order (technique[0]=BasicEffect, [1]=BasicEffect_NoFog, ...)");
    }

    [Fact]
    public async Task DirectX_BasicEffect_StructurallyMatchesGoldenOrKnownDivergence()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        CancellationToken ct = cts.Token;

        var (mgfx, error) = await CompileAsync("BasicEffect.fx", PlatformTarget.DirectX, ct);
        error.Should().BeNull();

        string goldenPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", "DirectX_11", "BasicEffect.mgfx");
        File.Exists(goldenPath).Should().BeTrue($"golden expected at {goldenPath}");

        MgfxBlobReader subject = MgfxBlobReader.Parse(mgfx!);
        MgfxBlobReader golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));

        // Technique shape must match the golden exactly (count, names, order, pass counts).
        subject.TechniqueCount.Should().Be(golden.TechniqueCount);
        subject.Techniques.Select(t => t.Name).Should().Equal(golden.Techniques.Select(t => t.Name));
        for (int t = 0; t < golden.TechniqueCount; t++)
            subject.Techniques[t].PassCount.Should().Be(golden.Techniques[t].PassCount,
                $"technique '{golden.Techniques[t].Name}' pass count must match the golden");

        // Constant-buffer SIZE must match the golden on DX (the runtime SetValue layout).
        var goldCbBySize = golden.ConstantBuffers.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var sub in subject.ConstantBuffers)
        {
            if (goldCbBySize.TryGetValue(sub.Name, out var gold))
                sub.Size.Should().Be(gold.Size,
                    $"cbuffer '{sub.Name}' size must match the golden on DirectX");
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
            subjByName.Should().ContainKey(gold.Name,
                $"golden value-class parameter '{gold.Name}' must be reachable by name");
            var sub = subjByName[gold.Name];
            (sub.Class, sub.Type, sub.Rows, sub.Columns).Should().Be(
                (gold.Class, gold.Type, gold.Rows, gold.Columns),
                $"parameter '{gold.Name}' value-class shape must match the golden");
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

        mgfx.Should().BeNull();
        error.Should().NotBeNull();
        error!.Code.Should().Be("SD0010",
            "the OpenGL macro-model gap surfaces as a loud SD0010, never a native crash");
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

        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainSingle(e => e.Code == "SD0010");
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
