#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 43 (F1/F1b/F2/F9) structural golden gate: for every fixture with a real
/// <c>mgfxc</c> 3.8.2.1105 golden that carries pass render states or baked sampler
/// states, ShadowDusk's <c>.mgfx</c> must contain the SAME state records the golden
/// does — field for field, in MonoGame 3.8.2's fixed wire layout. Both sides are
/// parsed by the same <see cref="MgfxBlobReader"/>, which reads the layout exactly
/// as MonoGame's <c>Effect.ReadPasses</c>/<c>Shader</c> reader does and verifies the
/// trailing MGFX signature (MonoGame's own desync guard) — so this grades ShadowDusk
/// against the golden + the real reader's layout, not against ShadowDusk's writer.
///
/// <para>Goldens were produced on win-x64 with the real
/// <c>dotnet-mgfxc 3.8.2.1105</c>: <c>mgfxc &lt;fx&gt; &lt;out&gt; /Profile:{OpenGL|DirectX_11}</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MgfxStateGoldenMatchTests
{
    public static IEnumerable<object[]> StateFixtures()
    {
        foreach (string profile in new[] { "OpenGL", "DirectX_11" })
        {
            yield return new object[] { "render-states", profile };
            yield return new object[] { "StateBlendAdditive", profile };
            yield return new object[] { "StateDepthStencil", profile };
            yield return new object[] { "StateRasterizer", profile };
        }
    }

    [Theory]
    [MemberData(nameof(StateFixtures))]
    public async Task PassRenderStates_MatchMgfxcGolden(string stem, string profile)
    {
        (MgfxBlobReader subject, MgfxBlobReader golden) = await CompileAndLoadGoldenAsync(stem, profile);

        subject.TechniqueCount.Should().Be(golden.TechniqueCount);
        for (int t = 0; t < golden.TechniqueCount; t++)
        {
            TechniqueInfo gTech = golden.Techniques[t];
            TechniqueInfo sTech = subject.Techniques[t];
            sTech.PassCount.Should().Be(gTech.PassCount, $"technique '{gTech.Name}' pass count");

            for (int p = 0; p < gTech.PassCount; p++)
            {
                PassInfo gPass = gTech.Passes[p];
                PassInfo sPass = sTech.Passes[p];

                // Record equality — every field of MonoGame's fixed layout, including
                // the defaulted ones (mgfxc's state-object constructor defaults).
                sPass.BlendState.Should().Be(gPass.BlendState,
                    $"pass '{gPass.Name}' blend state must match the mgfxc golden");
                sPass.DepthStencilState.Should().Be(gPass.DepthStencilState,
                    $"pass '{gPass.Name}' depth-stencil state must match the mgfxc golden");
                sPass.RasterizerState.Should().Be(gPass.RasterizerState,
                    $"pass '{gPass.Name}' rasterizer state must match the mgfxc golden");
            }
        }
    }

    [Theory]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    public async Task SamplerStates_MatchMgfxcGolden(string profile)
    {
        (MgfxBlobReader subject, MgfxBlobReader golden) =
            await CompileAndLoadGoldenAsync("SamplerStatesFull", profile);

        // Key by sampler slot: shader/record order can legally differ between the
        // two compilers, but the per-slot baked state is the runtime contract
        // (MonoGame applies SamplerStates[slot] at EffectPass.Apply).
        var goldenBySlot  = golden.Samplers.ToDictionary(s => s.SamplerSlot);
        var subjectBySlot = subject.Samplers.ToDictionary(s => s.SamplerSlot);

        subjectBySlot.Keys.Should().BeEquivalentTo(goldenBySlot.Keys, "same sampler slots");
        foreach ((byte slot, MgfxSamplerRecord gold) in goldenBySlot)
        {
            MgfxSamplerRecord sub = subjectBySlot[slot];
            sub.State.Should().Be(gold.State,
                $"sampler slot {slot}: the baked sampler_state must match the mgfxc golden " +
                "(hasState, addressing, border color, combined filter, anisotropy, mip fields)");
            sub.State.Should().NotBeNull(
                $"sampler slot {slot} declares sampler_state members — hasState must be 1");
        }
    }

    [Theory]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    public async Task Annotations_GoldenParsesWithSameReader_NoBodiesEitherSide(string profile)
    {
        (MgfxBlobReader subject, MgfxBlobReader golden) =
            await CompileAndLoadGoldenAsync("annotations", profile);

        // MgfxBlobReader reads annotations exactly as MonoGame 3.8.2 does (count only)
        // and verifies the trailing MGFX signature, so a successful parse of BOTH
        // files proves neither carries annotation bodies. The counts legitimately
        // differ: mgfxc drops annotations entirely (0), ShadowDusk preserves the
        // declared count (metadata-only; MonoGame allocates the slots and reads on).
        golden.ParameterAnnotationCounts.Should().BeEmpty("mgfxc writes annotation count 0");
        subject.ParameterAnnotationCounts.Should().ContainKey("TintColor");
        subject.ParameterAnnotationCounts["TintColor"].Should().Be(2);
    }

    private static async Task<(MgfxBlobReader Subject, MgfxBlobReader Golden)>
        CompileAndLoadGoldenAsync(string stem, string profile)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var ct = cts.Token;

        string fxPath = TestHelpers.FixturePath(stem + ".fx");
        File.Exists(fxPath).Should().BeTrue($".fx fixture must exist at {fxPath}");
        string source = await File.ReadAllTextAsync(fxPath, ct);

        PlatformTarget target = profile == "OpenGL" ? PlatformTarget.OpenGL : PlatformTarget.DirectX;
        var result = await new EffectCompiler().CompileAsync(
            source,
            new CompilerOptions { Target = target, SourceFileName = fxPath },
            ct);
        result.IsSuccess.Should().BeTrue(
            because: result.IsFailure
                ? string.Join(" | ", result.Error.Select(e => e.FxcFormattedMessage))
                : "the fixture must compile");

        string goldenPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", profile, stem + ".mgfx");
        File.Exists(goldenPath).Should().BeTrue($"mgfxc golden must exist at {goldenPath}");

        return (
            MgfxBlobReader.Parse(result.Value.Data),
            MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new DirectoryNotFoundException("repo root not found");
    }
}
