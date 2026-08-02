#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Reflection;

/// <summary>
/// GitHub issue #189: every golden-backed OpenGL fixture's SAMPLER TABLE must match the
/// committed <c>mgfxc</c> golden — the uniform name, the texture unit, and which texture
/// parameter each record binds. That triple is exactly what MonoGame's GL runtime uses:
/// <c>glUniform1i(glGetUniformLocation(record.Name), record.TextureSlot)</c> followed by
/// <c>textures[record.TextureSlot] = Parameters[record.Parameter].Data</c>.
///
/// <para><b>Why this exists as a separate, DISCOVERED sweep.</b> Issue #189 was fixed twice, and
/// both times an intermediate rule looked right against a handful of hand-authored probes and
/// then failed on a shape nobody had tried. Hand-picked samples are how that happens:
/// <c>MgfxParameterMatchTests</c>, the closest existing golden comparison, runs against a
/// hand-maintained 13-entry list. This one enumerates
/// <c>tests/fixtures/golden/OpenGL/*.mgfx</c> from DISK, so it covers every golden the corpus
/// has and automatically covers any golden added later — a new fixture cannot quietly sit
/// outside the sweep.</para>
///
/// <para><b>The one normalization, and why it is not a loophole.</b> <c>mgfxc</c> spells a GL
/// texture parameter with MojoShader's combined form (<c>TextureSampler+DiffuseMap</c>) while
/// ShadowDusk keeps the plain texture name, and <c>FxPreParser</c>'s synthesized texture carries
/// an <c>_SDTexture</c> suffix. Both are deliberate, recorded divergences (see
/// <c>project_decisions.md</c>) that MonoGame cannot observe, because it resolves a sampler's
/// texture through the record's parameter INDEX and never its name. Stripping them is what lets
/// this assert the thing under test — the SLOTS — rather than re-litigating naming.</para>
/// </summary>
public sealed class GlSamplerSlotCorpusTests
{
    /// <summary>
    /// Fixtures whose techniques are defined through a macro, which the pre-parser cannot see
    /// because it counts techniques BEFORE preprocessing. They fail with <c>SD0010</c> on the
    /// OpenGL path and have nothing to compare. This is Phase 41 GAP-1's GL half, tracked as an
    /// open, externally-blocked gap in <c>docs/validation-matrix.md</c> §7 — NOT anything this
    /// sweep introduced. Listed by name rather than caught by a broad "any failure is fine"
    /// rule, so a NEW compile failure fails the test instead of silently joining the skip set.
    /// </summary>
    private static readonly HashSet<string> MacroTechniqueGap = new(StringComparer.Ordinal)
    {
        "AlphaTestEffect", "BasicEffect", "DualTextureEffect", "EnvironmentMapEffect",
        "PenumbraHull", "PenumbraLight", "PenumbraShadow", "PenumbraTexture",
        "SkinnedEffect", "SpriteEffect",
    };

    public static IEnumerable<object[]> GoldenBackedFixtures()
    {
        string goldenDir = Path.Combine(RepoRoot(), "tests", "fixtures", "golden", "OpenGL");
        foreach (string golden in Directory.EnumerateFiles(goldenDir, "*.mgfx").OrderBy(p => p, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(golden) };
    }

    [Theory]
    [MemberData(nameof(GoldenBackedFixtures))]
    [Trait("Category", "Integration")]
    [Trait("Platform", "OpenGL")]
    public async Task SamplerTable_MatchesMgfxcGolden(string stem)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        CancellationToken ct = cts.Token;

        string fxPath = FindSource(stem);
        string source = await File.ReadAllTextAsync(fxPath, ct);

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName  = fxPath,
        }, ct);

        if (result.IsFailure)
        {
            // A known-gap fixture is allowed to fail, but ONLY with the diagnostic that gap
            // produces. Any other failure, on any fixture, is a real regression.
            MacroTechniqueGap.Contains(stem).ShouldBeTrue(
                $"{stem} failed to compile for OpenGL and is not a recorded known-gap fixture: " +
                string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
            result.Error.Any(e => e.Code == "SD0010").ShouldBeTrue(
                $"{stem} is recorded as a macro-defined-technique gap, so it must fail with SD0010, but got: " +
                string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
            return;
        }

        // A fixture in the known-gap set that starts COMPILING is good news, but it means the
        // list is stale and the fixture should be moved back under the real comparison.
        MacroTechniqueGap.Contains(stem).ShouldBeFalse(
            $"{stem} is listed as a macro-technique gap but now compiles for OpenGL — remove it " +
            "from MacroTechniqueGap so its sampler table is actually compared.");

        string goldenPath = Path.Combine(RepoRoot(), "tests", "fixtures", "golden", "OpenGL", stem + ".mgfx");
        MgfxBlobReader golden  = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));
        MgfxBlobReader subject = MgfxBlobReader.Parse(result.Value.Data);

        IReadOnlyList<string> expected = Describe(golden);
        IReadOnlyList<string> actual   = Describe(subject);

        actual.ShouldBe(expected, customMessage:
            $"{stem}: the OpenGL sampler table must match mgfxc's — uniform name, texture unit, " +
            "and bound texture parameter. A mismatch means the shader samples a different " +
            "texture than the reference build does (issue #189).");
    }

    /// <summary>
    /// One comparable line per sampler record: the uniform the GL runtime looks up, the unit it
    /// binds, and the texture behind it.
    ///
    /// <para>Keyed on the shader's STAGE rather than its index in the shader table, because the
    /// two compilers order that table differently — <c>mgfxc</c> writes the pixel shader first,
    /// ShadowDusk writes the vertex shader first. That is a pre-existing, purely structural
    /// difference (MonoGame reaches a shader through the pass's own vsIndex/psIndex, never by
    /// table position), and letting it fail here would have this sweep reporting an ordering
    /// artefact as a sampler mis-binding. Sorted by unit so record emission order within a stage
    /// is likewise not part of the contract — only the mapping is.</para>
    /// </summary>
    private static IReadOnlyList<string> Describe(MgfxBlobReader mgfx) =>
        mgfx.Samplers
            .Select(s => new { Rec = s, Stage = mgfx.Shaders[s.ShaderIndex].IsVertex ? "vs" : "ps" })
            .OrderBy(x => x.Stage, StringComparer.Ordinal)
            .ThenBy(x => x.Rec.TextureSlot)
            .Select(x => $"{x.Stage} {x.Rec.Name} unit={x.Rec.TextureSlot} " +
                         $"texture={NormalizeTextureName(mgfx.Parameters[x.Rec.Parameter].Name)}")
            .ToList();

    /// <summary>
    /// Reduces both compilers' spellings of a GL texture parameter to the texture's own name.
    /// mgfxc uses MojoShader's <c>&lt;sampler&gt;+&lt;texture&gt;</c>; ShadowDusk keeps the plain
    /// name and suffixes a synthesized one with <c>_SDTexture</c>. Both are recorded, deliberate
    /// divergences that MonoGame cannot observe (it binds by parameter INDEX, never by name).
    /// </summary>
    private static string NormalizeTextureName(string name)
    {
        int plus = name.IndexOf('+');
        if (plus >= 0)
            name = name[(plus + 1)..];
        const string synthesized = "_SDTexture";
        return name.EndsWith(synthesized, StringComparison.Ordinal)
            ? name[..^synthesized.Length]
            : name;
    }

    private static string FindSource(string stem)
    {
        string shaders = Path.Combine(RepoRoot(), "tests", "fixtures", "shaders");
        string? hit = Directory.EnumerateFiles(shaders, stem + ".fx", SearchOption.AllDirectories)
                               .OrderBy(p => p, StringComparer.Ordinal)
                               .FirstOrDefault();
        hit.ShouldNotBeNull($"a golden exists for '{stem}' but no {stem}.fx was found under {shaders}");
        return hit!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("could not locate the repository root from the test output directory");
        return dir!.FullName;
    }
}
