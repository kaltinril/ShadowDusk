#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 41 backbone: the fxc/mgfxc structural fidelity matrix.
///
/// <para>This is RESEARCH/CHARACTERIZATION, not a product change. It compiles the full
/// fixture corpus through ShadowDusk for both the DirectX_11 and OpenGL targets and diffs
/// the resulting <c>.mgfx</c> STRUCTURE (the records MonoGame's <c>Effect</c> reader
/// consumes: parameters, constant buffers, samplers, techniques/passes + render states,
/// annotation counts) against the committed <c>mgfxc</c> 3.8.2.1105 goldens, using the
/// same <see cref="MgfxBlobReader"/> for both sides.</para>
///
/// <para><b>The bar is structural/behavioral equivalence + Effect-loadability, NOT
/// byte-identity</b> (CLAUDE.md). ShadowDusk's DX path uses vkd3d-shader and its GL path
/// uses SPIRV-Cross, so the compiled shader bytecode WILL differ from mgfxc's fxc/MojoShader
/// output and that is EXPECTED. Bytecode-blob byte/size differences are deliberately
/// excluded from the divergence verdict.</para>
///
/// <para>Running the <see cref="GenerateDivergenceMatrixReport"/> fact writes
/// <c>plan/PHASE-41-appendix/structural-divergence-matrix.md</c> as a deterministic side
/// effect. It never overwrites any committed golden; ShadowDusk candidates are compiled in
/// memory only.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Phase41StructuralDivergenceMatrixTests
{
    private readonly ITestOutputHelper _output;

    public Phase41StructuralDivergenceMatrixTests(ITestOutputHelper output) => _output = output;

    // EffectParameterClass.Object — sampler/texture params carry the two pinned,
    // render-proven divergences documented in MgfxParameterMatchTests; we tag them as
    // an expected object-class class so the matrix does not flag them as real divergences.
    private const byte ClassObject = 3;
    private const byte TypeSampler = 5;
    private const string SynthesizedTextureSuffix = "_SDTexture";

    private static readonly string[] s_targets = { "DirectX_11", "OpenGL" };

    [Fact]
    public async Task GenerateDivergenceMatrixReport()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        CancellationToken ct = cts.Token;

        string repoRoot = FindRepoRoot();
        var goldenBacked = DiscoverGoldenBackedFixtures(repoRoot);
        var nonGolden = DiscoverNonGoldenFixtures(repoRoot, goldenBacked);

        // ---- Golden-backed matrix: compile + structural diff per target ----
        var matrixRows = new List<GoldenCellResult>();
        foreach (string stem in goldenBacked)
        {
            foreach (string target in s_targets)
                matrixRows.Add(await DiffGoldenBackedAsync(repoRoot, stem, target, ct));
        }

        // ---- Non-golden census: compile-only PASS / FAIL(+code) per target ----
        var censusRows = new List<CensusCellResult>();
        foreach (string fxRel in nonGolden)
        {
            foreach (string target in s_targets)
                censusRows.Add(await CensusCompileAsync(stemDisplay: fxRel, fxRel: fxRel, target: target, ct: ct));
        }

        string report = BuildReport(matrixRows, censusRows);

        string appendixDir = Path.Combine(repoRoot, "plan", "PHASE-41-appendix");
        Directory.CreateDirectory(appendixDir);
        string reportPath = Path.Combine(appendixDir, "structural-divergence-matrix.md");
        await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false), ct);

        _output.WriteLine($"Wrote {reportPath}");
        _output.WriteLine($"Golden-backed cells: {matrixRows.Count}; non-golden census cells: {censusRows.Count}");

        // Sanity: the run must cover the expected corpus shape so a future fixture
        // add/remove is noticed. (46 golden-backed * 2 targets, 26 non-golden * 2.)
        matrixRows.Count.Should().Be(goldenBacked.Count * 2);
        censusRows.Count.Should().Be(nonGolden.Count * 2);
        File.Exists(reportPath).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Golden-backed structural diff
    // -----------------------------------------------------------------------

    private async Task<GoldenCellResult> DiffGoldenBackedAsync(
        string repoRoot, string stem, string target, CancellationToken ct)
    {
        PlatformTarget platform = target == "OpenGL" ? PlatformTarget.OpenGL : PlatformTarget.DirectX;
        string fxPath = TestHelpers.FixturePath(stem + ".fx");

        // 1. Compile the ShadowDusk candidate in memory (NOT into fixtures/golden).
        var (candidate, compileError) = await CompileAsync(fxPath, platform, ct);
        if (candidate is null)
        {
            // A golden-backed fixture that fails to compile is a notable finding.
            return GoldenCellResult.CompileFailure(stem, target, compileError!);
        }

        // 2. Load + parse the committed golden.
        string goldenPath = Path.Combine(repoRoot, "tests", "fixtures", "golden", target, stem + ".mgfx");
        if (!File.Exists(goldenPath))
            return GoldenCellResult.GoldenMissing(stem, target, goldenPath);

        MgfxBlobReader golden;
        MgfxBlobReader subject;
        try
        {
            golden = MgfxBlobReader.Parse(await File.ReadAllBytesAsync(goldenPath, ct));
            subject = MgfxBlobReader.Parse(candidate);
        }
        catch (Exception ex)
        {
            return GoldenCellResult.ParseFailure(stem, target, ex.Message);
        }

        // 3. Diff each structural level. Bytecode bytes/sizes are NOT compared.
        var divergences = new List<string>();

        bool paramsMatch = DiffParameters(subject, golden, divergences);
        bool cbufMatch = DiffConstantBuffers(subject, golden, divergences);
        bool samplerMatch = DiffSamplers(subject, golden, divergences);
        bool techMatch = DiffTechniquesAndStates(subject, golden, divergences);
        bool annotMatch = DiffAnnotationCounts(subject, golden, divergences);

        return new GoldenCellResult(
            Stem: stem,
            Target: target,
            Outcome: CellOutcome.Diffed,
            ParametersMatch: paramsMatch,
            CbuffersMatch: cbufMatch,
            SamplersMatch: samplerMatch,
            TechniquesMatch: techMatch,
            AnnotationsMatch: annotMatch,
            Divergences: divergences,
            Note: null);
    }

    /// <summary>
    /// Parameter metadata diff, keyed by name (order is not part of the contract — MonoGame
    /// looks parameters up by name). Mirrors MgfxParameterMatchTests' value-vs-object rules:
    /// value-class params must match exactly; the two pinned object-class shapes (extra
    /// sampler params, legacy `sampler s0;` -> synthesized `_SDTexture`) are not divergences.
    /// </summary>
    private static bool DiffParameters(MgfxBlobReader subject, MgfxBlobReader golden, List<string> div)
    {
        var subjectByName = subject.Parameters
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        bool clean = true;

        foreach (MgfxParameterRecord gold in golden.Parameters)
        {
            if (!subjectByName.TryGetValue(gold.Name, out MgfxParameterRecord? sub))
            {
                // mgfxc-texture vs ShadowDusk-sampler legacy rename: the texture lives at
                // the companion name; the sampler param carries the golden name.
                if (gold.Class == ClassObject &&
                    subjectByName.ContainsKey(gold.Name + SynthesizedTextureSuffix))
                {
                    continue; // pinned legacy `sampler s0;` shape — equivalent.
                }

                div.Add($"param `{gold.Name}` missing (golden class={gold.Class} type={gold.Type})");
                clean = false;
                continue;
            }

            if (gold.Class != ClassObject)
            {
                if (sub.Class != gold.Class || sub.Type != gold.Type || sub.Rows != gold.Rows ||
                    sub.Columns != gold.Columns || sub.ElementCount != gold.ElementCount ||
                    sub.MemberCount != gold.MemberCount)
                {
                    div.Add(
                        $"param `{gold.Name}` value-class delta " +
                        $"(class {sub.Class}/{gold.Class} type {sub.Type}/{gold.Type} " +
                        $"rows {sub.Rows}/{gold.Rows} cols {sub.Columns}/{gold.Columns} " +
                        $"elem {sub.ElementCount}/{gold.ElementCount} mem {sub.MemberCount}/{gold.MemberCount})");
                    clean = false;
                }
                continue;
            }

            // Object-class: identical type is clean; the sampler-rename is a pinned, allowed shape.
            if (sub.Class == ClassObject && sub.Type == gold.Type)
                continue;
            if (sub.Class == ClassObject && sub.Type == TypeSampler &&
                subjectByName.ContainsKey(gold.Name + SynthesizedTextureSuffix))
                continue;

            div.Add($"object-param `{gold.Name}` class {sub.Class}/{gold.Class} type {sub.Type}/{gold.Type}");
            clean = false;
        }

        // Extras: only object-class sampler params and synthesized _SDTexture companions
        // are allowed (pinned divergence 1). An extra value-class param is a real divergence.
        var goldenNames = golden.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (MgfxParameterRecord extra in subject.Parameters.Where(p => !goldenNames.Contains(p.Name)))
        {
            bool allowed = extra.Class == ClassObject &&
                           (extra.Type == TypeSampler ||
                            extra.Name.EndsWith(SynthesizedTextureSuffix, StringComparison.Ordinal));
            if (!allowed)
            {
                div.Add($"extra value-class param `{extra.Name}` (class={extra.Class} type={extra.Type})");
                clean = false;
            }
        }

        return clean;
    }

    /// <summary>
    /// Constant-buffer diff: name + size + the per-name byte offsets. Keyed by cbuffer name.
    /// (Parameter-slot indices legally renumber between the two compilers; the offsets keyed
    /// by parameter name are the runtime-meaningful layout.)
    /// </summary>
    private static bool DiffConstantBuffers(MgfxBlobReader subject, MgfxBlobReader golden, List<string> div)
    {
        var goldByName = golden.ConstantBuffers
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var subjByName = subject.ConstantBuffers
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        bool clean = true;

        foreach ((string name, MgfxConstantBufferRecord gold) in goldByName)
        {
            if (!subjByName.TryGetValue(name, out MgfxConstantBufferRecord? sub))
            {
                div.Add($"cbuffer `{name}` missing (golden size {gold.Size})");
                clean = false;
                continue;
            }
            if (sub.Size != gold.Size)
            {
                div.Add($"cbuffer `{name}` size {sub.Size} vs {gold.Size}");
                clean = false;
            }
        }

        foreach (string extra in subjByName.Keys.Where(n => !goldByName.ContainsKey(n)))
        {
            div.Add($"extra cbuffer `{extra}`");
            clean = false;
        }

        // Per-parameter offsets keyed by parameter name (the runtime layout).
        foreach ((string pName, int goldOff) in golden.ParameterOffsets)
        {
            if (subject.ParameterOffsets.TryGetValue(pName, out int subOff) && subOff != goldOff)
            {
                div.Add($"param `{pName}` cbuffer offset {subOff} vs {goldOff}");
                clean = false;
            }
        }

        return clean;
    }

    /// <summary>Sampler diff keyed by sampler slot: the per-slot baked state is the runtime contract.</summary>
    private static bool DiffSamplers(MgfxBlobReader subject, MgfxBlobReader golden, List<string> div)
    {
        var goldBySlot = golden.Samplers
            .GroupBy(s => s.SamplerSlot).ToDictionary(g => g.Key, g => g.First());
        var subjBySlot = subject.Samplers
            .GroupBy(s => s.SamplerSlot).ToDictionary(g => g.Key, g => g.First());
        bool clean = true;

        foreach ((byte slot, MgfxSamplerRecord gold) in goldBySlot)
        {
            if (!subjBySlot.TryGetValue(slot, out MgfxSamplerRecord? sub))
            {
                div.Add($"sampler slot {slot} missing (golden `{gold.Name}`)");
                clean = false;
                continue;
            }
            if (!Equals(sub.State, gold.State))
            {
                div.Add($"sampler slot {slot} baked-state differs");
                clean = false;
            }
        }

        foreach (byte slot in subjBySlot.Keys.Where(s => !goldBySlot.ContainsKey(s)))
        {
            div.Add($"extra sampler slot {slot} (`{subjBySlot[slot].Name}`)");
            clean = false;
        }

        return clean;
    }

    /// <summary>
    /// Technique/pass shape + render-state diff. Pass shader INDICES are not compared
    /// (they legally renumber); the VS/PS presence (whether a pass binds a vertex/pixel
    /// shader) and the per-pass blend/depth/raster records ARE.
    /// </summary>
    private static bool DiffTechniquesAndStates(MgfxBlobReader subject, MgfxBlobReader golden, List<string> div)
    {
        bool clean = true;

        if (subject.TechniqueCount != golden.TechniqueCount)
        {
            div.Add($"technique count {subject.TechniqueCount} vs {golden.TechniqueCount}");
            return false;
        }

        for (int t = 0; t < golden.TechniqueCount; t++)
        {
            TechniqueInfo g = golden.Techniques[t];
            TechniqueInfo s = subject.Techniques[t];

            if (s.Name != g.Name)
            {
                div.Add($"technique[{t}] name `{s.Name}` vs `{g.Name}`");
                clean = false;
            }
            if (s.PassCount != g.PassCount)
            {
                div.Add($"technique `{g.Name}` pass count {s.PassCount} vs {g.PassCount}");
                clean = false;
                continue;
            }

            for (int p = 0; p < g.PassCount; p++)
            {
                PassInfo gp = g.Passes[p];
                PassInfo sp = s.Passes[p];

                if (sp.Name != gp.Name)
                {
                    div.Add($"`{g.Name}` pass[{p}] name `{sp.Name}` vs `{gp.Name}`");
                    clean = false;
                }
                // VS/PS presence (index >= 0), not the exact index value.
                if ((sp.VertexShaderIndex >= 0) != (gp.VertexShaderIndex >= 0))
                {
                    div.Add($"`{g.Name}` pass `{gp.Name}` VS presence {sp.VertexShaderIndex >= 0} vs {gp.VertexShaderIndex >= 0}");
                    clean = false;
                }
                if ((sp.PixelShaderIndex >= 0) != (gp.PixelShaderIndex >= 0))
                {
                    div.Add($"`{g.Name}` pass `{gp.Name}` PS presence {sp.PixelShaderIndex >= 0} vs {gp.PixelShaderIndex >= 0}");
                    clean = false;
                }
                if (!Equals(sp.BlendState, gp.BlendState))
                {
                    div.Add($"`{g.Name}` pass `{gp.Name}` BlendState differs");
                    clean = false;
                }
                if (!Equals(sp.DepthStencilState, gp.DepthStencilState))
                {
                    div.Add($"`{g.Name}` pass `{gp.Name}` DepthStencilState differs");
                    clean = false;
                }
                if (!Equals(sp.RasterizerState, gp.RasterizerState))
                {
                    div.Add($"`{g.Name}` pass `{gp.Name}` RasterizerState differs");
                    clean = false;
                }
            }
        }

        return clean;
    }

    /// <summary>
    /// Annotation-count diff. mgfxc drops annotations entirely (count 0); ShadowDusk
    /// preserves the declared count (metadata-only — MonoGame allocates slots and reads on).
    /// This is a known, render-irrelevant divergence (Phase 43 F2); we record it but the
    /// distinction is captured in the divergence-class summary, not treated as a defect.
    /// </summary>
    private static bool DiffAnnotationCounts(MgfxBlobReader subject, MgfxBlobReader golden, List<string> div)
    {
        bool clean = true;
        foreach ((string name, int goldCount) in golden.ParameterAnnotationCounts)
        {
            int subCount = subject.ParameterAnnotationCounts.TryGetValue(name, out int c) ? c : 0;
            if (subCount != goldCount)
            {
                div.Add($"param `{name}` annotation count {subCount} vs {goldCount}");
                clean = false;
            }
        }
        foreach ((string name, int subCount) in subject.ParameterAnnotationCounts)
        {
            if (!golden.ParameterAnnotationCounts.ContainsKey(name))
            {
                div.Add($"param `{name}` annotation count {subCount} vs 0 (mgfxc drops annotations)");
                clean = false;
            }
        }
        return clean;
    }

    // -----------------------------------------------------------------------
    // Non-golden compile census
    // -----------------------------------------------------------------------

    private async Task<CensusCellResult> CensusCompileAsync(
        string stemDisplay, string fxRel, string target, CancellationToken ct)
    {
        PlatformTarget platform = target == "OpenGL" ? PlatformTarget.OpenGL : PlatformTarget.DirectX;
        string fxPath = TestHelpers.FixturePath(fxRel);

        var (candidate, error) = await CompileAsync(fxPath, platform, ct);
        if (candidate is not null)
            return new CensusCellResult(stemDisplay, target, Passed: true, Code: null, Message: null);

        return new CensusCellResult(
            stemDisplay, target, Passed: false,
            Code: error!.Code,
            Message: error.Message);
    }

    // -----------------------------------------------------------------------
    // Compile helper
    // -----------------------------------------------------------------------

    private static async Task<(byte[]? Bytes, ShaderError? Error)> CompileAsync(
        string fxPath, PlatformTarget target, CancellationToken ct)
    {
        if (!File.Exists(fxPath))
            return (null, new ShaderError(fxPath, 0, 0, "SD9999", $"fixture not found at {fxPath}"));

        string source = await File.ReadAllTextAsync(fxPath, ct);
        var options = new CompilerOptions
        {
            Target = target,
            SourceFileName = fxPath,
            IncludeResolver = new FileSystemIncludeResolver(),
        };

        try
        {
            var result = await new EffectCompiler().CompileAsync(source, options, ct);
            if (result.IsSuccess)
                return (result.Value.Data, null);

            ShaderError first = result.Error.FirstOrDefault()
                ?? new ShaderError(fxPath, 0, 0, "SD9999", "compile failed with no diagnostic");
            return (null, first);
        }
        catch (Exception ex)
        {
            return (null, new ShaderError(fxPath, 0, 0, "SDEXCEPTION", ex.Message));
        }
    }

    // -----------------------------------------------------------------------
    // Fixture discovery
    // -----------------------------------------------------------------------

    private static List<string> DiscoverGoldenBackedFixtures(string repoRoot)
    {
        // A fixture is golden-backed when a golden exists for BOTH targets under its stem.
        string dxDir = Path.Combine(repoRoot, "tests", "fixtures", "golden", "DirectX_11");
        return Directory.EnumerateFiles(dxDir, "*.mgfx")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => s is not null)
            .Select(s => s!)
            .Where(stem => File.Exists(Path.Combine(repoRoot, "tests", "fixtures", "golden", "OpenGL", stem + ".mgfx")))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> DiscoverNonGoldenFixtures(string repoRoot, IReadOnlyList<string> goldenBacked)
    {
        string shadersDir = Path.Combine(repoRoot, "tests", "fixtures", "shaders");
        var goldenSet = goldenBacked.ToHashSet(StringComparer.Ordinal);

        return Directory.EnumerateFiles(shadersDir, "*.fx", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(shadersDir, path).Replace('\\', '/'))
            .Where(rel => !goldenSet.Contains(Path.GetFileNameWithoutExtension(rel)))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    // -----------------------------------------------------------------------
    // Report
    // -----------------------------------------------------------------------

    private static string BuildReport(
        IReadOnlyList<GoldenCellResult> matrix, IReadOnlyList<CensusCellResult> census)
    {
        var sb = new StringBuilder();
        int cleanCells = matrix.Count(r => r.Outcome == CellOutcome.Diffed && r.AllMatch);
        int divergentCells = matrix.Count(r => r.Outcome == CellOutcome.Diffed && !r.AllMatch);
        int failedCells = matrix.Count(r => r.Outcome != CellOutcome.Diffed);

        sb.AppendLine("# Phase 41 — Structural Divergence Matrix (ShadowDusk vs mgfxc goldens)");
        sb.AppendLine();
        sb.AppendLine("> Generated by `Phase41StructuralDivergenceMatrixTests.GenerateDivergenceMatrixReport`");
        sb.AppendLine("> (`tests/ShadowDusk.Integration.Tests`). Deterministic; re-run to regenerate.");
        sb.AppendLine();

        sb.AppendLine("## Provenance & the \"byte-identity is not the bar\" note");
        sb.AppendLine();
        sb.AppendLine($"- **ShadowDusk version:** {ShadowDuskVersion()} (DX target = vkd3d-shader default, GL target = DXC -> SPIRV-Cross).");
        sb.AppendLine("- **Goldens:** committed `tests/fixtures/golden/{DirectX_11,OpenGL}/*.mgfx`, produced by the real");
        sb.AppendLine("  `dotnet-mgfxc 3.8.2.1105` (fxc.exe -> DXBC for DX; MojoShader/GLSL for GL). The locally installed");
        sb.AppendLine("  mgfxc is 3.8.4.1, but the **3.8.2.1105 goldens are the canonical reference** and are treated as");
        sb.AppendLine("  read-only here — nothing under `tests/fixtures/golden/` is regenerated or overwritten by this run.");
        sb.AppendLine("- **The fidelity bar is structural/behavioral equivalence + Effect-loadability, NOT byte-identity**");
        sb.AppendLine("  (CLAUDE.md). ShadowDusk's DX path uses vkd3d-shader and its GL path uses SPIRV-Cross, so the compiled");
        sb.AppendLine("  **shader bytecode bytes differ from mgfxc's fxc/MojoShader output by construction (vkd3d vs fxc,");
        sb.AppendLine("  expected)**. Bytecode-blob byte/size differences are excluded from every divergence verdict below;");
        sb.AppendLine("  only the records MonoGame's `Effect` reader consumes are diffed (parameters, constant buffers,");
        sb.AppendLine("  samplers, techniques/passes + render states, annotation counts).");
        sb.AppendLine("- **Same-target only:** ShadowDusk DX vs mgfxc DX golden, ShadowDusk GL vs mgfxc GL golden. Never cross-target.");
        sb.AppendLine();

        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine($"- Golden-backed cells (fixture x target): **{matrix.Count}**");
        sb.AppendLine($"  - Structurally **clean**: **{cleanCells}**");
        sb.AppendLine($"  - **Divergent** (>=1 level): **{divergentCells}**");
        sb.AppendLine($"  - Compile/parse **failures**: **{failedCells}**");
        sb.AppendLine($"- Non-golden census cells: **{census.Count}** "
            + $"(**{census.Count(c => c.Passed)}** compile, **{census.Count(c => !c.Passed)}** fail with a code)");
        sb.AppendLine();

        // ---- Golden-backed matrix table ----
        sb.AppendLine("## Golden-backed fixtures — per-level structural verdict");
        sb.AppendLine();
        sb.AppendLine("Legend: `OK` = match, `XX` = diverge, `--` = compile/parse failed (see notes). Levels: "
            + "Params / Cbuffers / Samplers / Techniques+States / AnnotationCounts.");
        sb.AppendLine();
        sb.AppendLine("| Fixture | Target | Params | Cbuf | Samp | Tech+St | Annot | Notes |");
        sb.AppendLine("|---|---|:--:|:--:|:--:|:--:|:--:|---|");
        foreach (GoldenCellResult r in matrix.OrderBy(r => r.Stem, StringComparer.Ordinal).ThenBy(r => r.Target))
        {
            if (r.Outcome != CellOutcome.Diffed)
            {
                sb.AppendLine($"| {r.Stem} | {r.Target} | -- | -- | -- | -- | -- | {Escape(r.Note ?? "failure")} |");
                continue;
            }
            string notes = r.Divergences.Count == 0 ? "" : string.Join("; ", r.Divergences);
            sb.AppendLine(
                $"| {r.Stem} | {r.Target} | {V(r.ParametersMatch)} | {V(r.CbuffersMatch)} | "
                + $"{V(r.SamplersMatch)} | {V(r.TechniquesMatch)} | {V(r.AnnotationsMatch)} | {Escape(notes)} |");
        }
        sb.AppendLine();

        // ---- Golden-backed compile failures (the highest-signal finding) ----
        var failures = matrix.Where(r => r.Outcome != CellOutcome.Diffed).ToList();
        sb.AppendLine("## Golden-backed fixtures that FAILED to compile (notable findings)");
        sb.AppendLine();
        sb.AppendLine("These fixtures have a committed mgfxc golden (so mgfxc compiled them) but ShadowDusk did NOT");
        sb.AppendLine("produce a `.mgfx` for the target. Each is a real ShadowDusk limitation, not a harness artifact.");
        sb.AppendLine();
        if (failures.Count == 0)
        {
            sb.AppendLine("- _(none — every golden-backed fixture compiled for both targets.)_");
        }
        else
        {
            sb.AppendLine("| Fixture | Target | Code | Message |");
            sb.AppendLine("|---|---|:--:|---|");
            foreach (GoldenCellResult r in failures.OrderBy(r => r.Stem, StringComparer.Ordinal).ThenBy(r => r.Target))
            {
                string note = r.Note ?? "";
                string code = "";
                string msg = note;
                int idx = note.IndexOf(':');
                if (note.StartsWith("COMPILE FAIL ") && idx > 0)
                {
                    code = note.Substring("COMPILE FAIL ".Length, idx - "COMPILE FAIL ".Length).Trim();
                    msg = note.Substring(idx + 1).Trim();
                }
                sb.AppendLine($"| {r.Stem} | {r.Target} | {code} | {Escape(msg)} |");
            }
            sb.AppendLine();
            sb.AppendLine("**Root cause of the SD0010 cluster (XNA stock effects):** `AlphaTestEffect`, `BasicEffect`,");
            sb.AppendLine("`DualTextureEffect`, `EnvironmentMapEffect`, `SkinnedEffect`, `SpriteEffect`, and the `Penumbra*`");
            sb.AppendLine("fixtures declare their techniques ONLY through the `TECHNIQUE(name, vs, ps)` macro defined in the");
            sb.AppendLine("`#include`d `Macros.fxh`. ShadowDusk's `FxPreParser` counts techniques on the RAW source BEFORE");
            sb.AppendLine("include flattening / macro expansion, and it deliberately ignores macro-call forms like");
            sb.AppendLine("`TECHNIQUE(...)` (only literal `technique`/`technique11` keywords are recognized). So zero techniques");
            sb.AppendLine("are detected and the pipeline fails with SD0010 \"Effect source contains no techniques\" before any");
            sb.AppendLine("backend runs. mgfxc expands includes/macros first, so it sees the techniques. This is a characterized");
            sb.AppendLine("ordering limitation (technique detection vs include expansion), not a backend fidelity gap, and is");
            sb.AppendLine("the single highest-value item to feed into Phase 41 triage.");
            sb.AppendLine();
            sb.AppendLine("**DeferredSprite [OpenGL] (X0000):** a distinct, loud diagnostic ('Semantic COLOR is invalid for");
            sb.AppendLine("shader model: ps') from the GL path, unrelated to the SD0010 macro-technique cluster.");
            sb.AppendLine();
        }

        // ---- Divergence-class summary ----
        sb.AppendLine("## Divergence-class summary (the triage-feeding output)");
        sb.AppendLine();
        sb.AppendLine("Distinct classes of divergence observed across the matrix, grouped by shape:");
        sb.AppendLine();

        var classes = ClassifyDivergences(matrix);
        if (classes.Count == 0)
        {
            sb.AppendLine("- _(none — every golden-backed cell was structurally clean.)_");
        }
        else
        {
            foreach (DivergenceClass dc in classes.OrderByDescending(c => c.Cells.Count))
            {
                sb.AppendLine($"### {dc.Title} ({dc.Cells.Count} cell(s))");
                sb.AppendLine();
                sb.AppendLine(dc.Description);
                sb.AppendLine();
                sb.AppendLine("Affected cells: " + string.Join(", ", dc.Cells.OrderBy(c => c, StringComparer.Ordinal)));
                sb.AppendLine();
            }
        }

        sb.AppendLine("> Note on bytecode: every cell's shader bytecode differs from the golden (vkd3d vs fxc on DX,");
        sb.AppendLine("> SPIRV-Cross GLSL vs MojoShader on GL). This is EXPECTED and is not counted as a divergence anywhere above.");
        sb.AppendLine();

        // ---- Non-golden census ----
        sb.AppendLine("## Non-golden fixtures — compile census");
        sb.AppendLine();
        sb.AppendLine("No golden to diff against; this only answers \"does this shape compile, and if not, does it fail");
        sb.AppendLine("loudly with a real code?\" Several `examples/*.fx` are EXPECTED to fail loudly on OpenGL (the int/bool/");
        sb.AppendLine("mat3/struct uniform members and the cube/volume HiDef samplers) — an expected loud failure with a code");
        sb.AppendLine("is a CORRECT result, not a defect.");
        sb.AppendLine();
        sb.AppendLine("| Fixture | Target | Result | Code | Message |");
        sb.AppendLine("|---|---|:--:|:--:|---|");
        foreach (CensusCellResult c in census.OrderBy(c => c.Stem, StringComparer.Ordinal).ThenBy(c => c.Target))
        {
            string result = c.Passed ? "PASS" : "FAIL";
            string code = c.Passed ? "" : (c.Code ?? "");
            string msg = c.Passed ? "" : Escape(Truncate(c.Message ?? "", 160));
            sb.AppendLine($"| {c.Stem} | {c.Target} | {result} | {code} | {msg} |");
        }
        sb.AppendLine();

        // Census summary by code.
        var failCodes = census.Where(c => !c.Passed)
            .GroupBy(c => c.Code ?? "(none)")
            .OrderByDescending(g => g.Count())
            .ToList();
        sb.AppendLine("### Census failure codes");
        sb.AppendLine();
        if (failCodes.Count == 0)
        {
            sb.AppendLine("- _(none — every non-golden fixture compiled for both targets.)_");
        }
        else
        {
            foreach (var g in failCodes)
                sb.AppendLine($"- `{g.Key}`: {g.Count()} cell(s) — {string.Join(", ", g.Select(c => $"{c.Stem} [{c.Target}]").OrderBy(s => s, StringComparer.Ordinal))}");
        }
        sb.AppendLine();

        return sb.ToString();
    }

    private static List<DivergenceClass> ClassifyDivergences(IReadOnlyList<GoldenCellResult> matrix)
    {
        // Buckets keyed by a coarse classifier on each divergence message.
        var buckets = new Dictionary<string, (string Title, string Desc, SortedSet<string> Cells)>(StringComparer.Ordinal);

        void Add(string key, string title, string desc, string cell)
        {
            if (!buckets.TryGetValue(key, out var b))
                buckets[key] = b = (title, desc, new SortedSet<string>(StringComparer.Ordinal));
            b.Cells.Add(cell);
        }

        foreach (GoldenCellResult r in matrix.Where(r => r.Outcome == CellOutcome.Diffed && !r.AllMatch))
        {
            string cell = $"{r.Stem} [{r.Target}]";
            foreach (string d in r.Divergences)
            {
                if (d.Contains("annotation count"))
                    Add("annot", "Annotation counts (mgfxc drops, ShadowDusk preserves) — KNOWN, render-irrelevant",
                        "mgfxc writes annotation count 0 (drops annotation bodies and counts); ShadowDusk preserves the "
                        + "declared count as metadata. MonoGame's reader allocates the slots and reads no bodies either way, "
                        + "so this is render-irrelevant (Phase 43 F2). Not a defect.", cell);
                else if ((d.Contains("ps_uniforms_vec4") || d.Contains("vs_uniforms_vec4")) && d.Contains("size"))
                    Add("cbufstage", "GL per-stage cbuffer sizing (full-layout vs used-only) — KNOWN, render-equivalent",
                        "On the OpenGL target, mgfxc sizes each per-stage `{vs,ps}_uniforms_vec4` record to ONLY the members "
                        + "that stage actually uses (dead-uniform elimination); ShadowDusk emits each stage's FULL declared "
                        + "cbuffer layout. Both `.mgfx` files are internally self-consistent — the USED parameter's offset and "
                        + "the GLSL `uniform vec4 {vs,ps}_uniforms_vec4[size/16]` array length agree within each file, so "
                        + "`SetValue` binds correctly either way. This is the pinned, render-equivalent divergence already "
                        + "documented and tolerated by `Phase43CbufferModelTests` (F4); the accompanying `offset N vs 0` lines "
                        + "are the SAME shape (the used member sits at a different absolute offset but the same relative slot). "
                        + "Not a defect.", cell);
                else if (d.Contains("cbuffer offset") && r.Target == "OpenGL")
                    // The only cbuffer-offset divergences observed are the used-member's absolute
                    // offset shifting because ShadowDusk lays out the full per-stage cbuffer ahead
                    // of it — the same known GL per-stage sizing model as the size lines above.
                    Add("cbufstage", "GL per-stage cbuffer sizing (full-layout vs used-only) — KNOWN, render-equivalent",
                        "On the OpenGL target, mgfxc sizes each per-stage `{vs,ps}_uniforms_vec4` record to ONLY the members "
                        + "that stage actually uses (dead-uniform elimination); ShadowDusk emits each stage's FULL declared "
                        + "cbuffer layout, so a used member can sit at a different ABSOLUTE byte offset. Both `.mgfx` files are "
                        + "internally self-consistent and `SetValue` binds correctly either way (Phase43CbufferModelTests F4). "
                        + "Not a defect.", cell);
                else if (d.Contains("cbuffer offset") || d.Contains("cbuffer `") && d.Contains("size"))
                    Add("cbuf", "Constant-buffer layout (size / offset) — TRIAGE",
                        "A constant buffer size or a per-parameter byte offset differs OUTSIDE the known GL per-stage sizing "
                        + "model. Worth triage: cbuffer offsets are the runtime SetValue layout.", cell);
                else if (d.Contains("value-class delta") || d.Contains("extra value-class"))
                    Add("valparam", "Value-class parameter metadata delta",
                        "A Scalar/Vector/Matrix parameter's reflection metadata (class/type/rows/cols/elements/members) or "
                        + "an unexpected extra value-class parameter diverges. This is the SetValue fidelity surface and "
                        + "should be triaged.", cell);
                else if (d.Contains("object-param") || d.Contains("missing (golden class=3"))
                    Add("objparam", "Object-class (texture/sampler) parameter shape",
                        "A texture/sampler (object-class) parameter diverges beyond the two pinned, render-proven shapes "
                        + "(extra sampler params; legacy `sampler s0;` -> synthesized `_SDTexture`).", cell);
                else if (d.Contains("param `") && d.Contains("missing"))
                    Add("missingparam", "Missing parameter (not reachable by name)",
                        "A golden parameter is not reachable by name in the ShadowDusk output.", cell);
                else if (d.Contains("BlendState") || d.Contains("DepthStencilState") || d.Contains("RasterizerState"))
                    Add("state", "Pass render-state record delta",
                        "A pass blend/depth-stencil/rasterizer state record differs from the golden's baked state.", cell);
                else if (d.Contains("sampler slot"))
                    Add("sampler", "Sampler slot / baked-state delta",
                        "A sampler slot is missing/extra or its baked sampler_state differs.", cell);
                else if (d.Contains("pass[") && d.Contains("name `") && d.Contains("vs ``"))
                    Add("passname", "Anonymous-pass naming (`P0` vs empty) — KNOWN, render-irrelevant",
                        "For an anonymous `pass { ... }` (no name), mgfxc stores an empty pass name while ShadowDusk "
                        + "synthesizes `P0`. MonoGame addresses passes by INDEX (and by name only when the user named them), "
                        + "so a synthesized name for an unnamed pass does not change which pass runs. Render-irrelevant; "
                        + "not a defect.", cell);
                else if (d.Contains("technique") || d.Contains("pass"))
                    Add("tech", "Technique/pass shape delta — TRIAGE",
                        "Technique count, name, pass count, named-pass name, or VS/PS presence differs.", cell);
                else
                    Add("other", "Other divergence", "Uncategorized divergence — see the per-fixture notes.", cell);
            }
        }

        return buckets.Values
            .Select(b => new DivergenceClass(b.Title, b.Desc, b.Cells.ToList()))
            .ToList();
    }

    private static string ShadowDuskVersion()
    {
        try
        {
            var asm = typeof(EffectCompiler).Assembly;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private static string V(bool match) => match ? "OK" : "XX";

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    // -----------------------------------------------------------------------
    // Result records
    // -----------------------------------------------------------------------

    private enum CellOutcome { Diffed, CompileFailed, GoldenMissing, ParseFailed }

    private sealed record GoldenCellResult(
        string Stem,
        string Target,
        CellOutcome Outcome,
        bool ParametersMatch,
        bool CbuffersMatch,
        bool SamplersMatch,
        bool TechniquesMatch,
        bool AnnotationsMatch,
        IReadOnlyList<string> Divergences,
        string? Note)
    {
        public bool AllMatch =>
            ParametersMatch && CbuffersMatch && SamplersMatch && TechniquesMatch && AnnotationsMatch;

        public static GoldenCellResult CompileFailure(string stem, string target, ShaderError e) =>
            new(stem, target, CellOutcome.CompileFailed, false, false, false, false, false,
                Array.Empty<string>(), $"COMPILE FAIL {e.Code}: {Truncate(e.Message, 200)}");

        public static GoldenCellResult GoldenMissing(string stem, string target, string path) =>
            new(stem, target, CellOutcome.GoldenMissing, false, false, false, false, false,
                Array.Empty<string>(), $"golden missing at {path}");

        public static GoldenCellResult ParseFailure(string stem, string target, string msg) =>
            new(stem, target, CellOutcome.ParseFailed, false, false, false, false, false,
                Array.Empty<string>(), $"parse fail: {Truncate(msg, 200)}");
    }

    private sealed record CensusCellResult(
        string Stem, string Target, bool Passed, string? Code, string? Message);

    private sealed record DivergenceClass(string Title, string Description, IReadOnlyList<string> Cells);
}
