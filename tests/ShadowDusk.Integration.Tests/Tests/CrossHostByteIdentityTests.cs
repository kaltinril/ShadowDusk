#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 37 verification-tail item (1): the cross-host byte-identity assertion.
///
/// <para>Core Design Constraint 3 promises deterministic output — and Phase 37 A/B/C
/// pinned one DXC, one SPIRV-Cross, and one vkd3d version on every OS precisely so the
/// bytes are identical everywhere, not merely "each host is self-consistent." The
/// same-host determinism tests (<c>DeterminismTests</c>,
/// <c>Compile_Deterministic_SameBytesOnRepeat</c>) cannot see a cross-OS divergence;
/// this class can: it compiles a representative corpus in-process and asserts every
/// output's SHA-256 against ONE committed manifest
/// (<c>tests/fixtures/golden/byte-identity/manifest.json</c>, generated on win-x64).
/// When this passes on ubuntu and macOS in CI, the Linux/macOS bytes are PROVEN equal
/// to the Windows bytes — which transfers the Windows rung-4 render proofs
/// (Phase 17/18/39-40) to those hosts byte-for-byte.</para>
///
/// <para>Targets covered: OpenGL (DXC → SPIRV-Cross → managed), DirectX with
/// <see cref="DxbcBackend.Vkd3d"/> (the cross-platform backend — deliberately NOT the
/// Windows-only d3dcompiler_47 oracle, which is host-dependent by design), and FNA
/// (vkd3d SM1–3 → fx_2_0). Inputs are deterministically normalized: source text is
/// read with line endings normalized to LF (git checkout EOL policy differs per OS and
/// is not the compiler's doing) and <see cref="CompilerOptions.SourceFileName"/> is the
/// fixed fixture-relative name, never an absolute host path —
/// <see cref="SourceFileName_DoesNotAffect_OutputBytes"/> proves that name never leaks
/// into the bytes anyway.</para>
///
/// <para><b>Regenerating</b> (after a legitimate, reviewed compiler-output change —
/// manifest churn is expected and reviewable, exactly like goldens): set
/// <c>SHADOWDUSK_REGENERATE_BYTE_MANIFEST=1</c> and run this class on win-x64; the
/// tests rewrite the committed manifest instead of asserting. See
/// <c>tests/fixtures/golden/byte-identity/README.md</c>.</para>
///
/// <para><b>Honesty rule:</b> if any OS produces different bytes, that is a real
/// fidelity finding in the per-OS native builds. Never "fix" it by loosening this to
/// structural equality or per-OS manifests — report the mismatching fixture×target
/// with both hashes (the failure message below carries them).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossHostByteIdentityTests
{
    private const string RegenerateEnvVar = "SHADOWDUSK_REGENERATE_BYTE_MANIFEST";
    private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(120);

    private static bool RegenerateRequested =>
        Environment.GetEnvironmentVariable(RegenerateEnvVar) == "1";

    // -------------------------------------------------------------------------
    // Corpus — breadth over cherry-picking: PS-only, VS-driven, multi-pass/
    // multi-technique, render states, annotations, platform macros, textures.
    // Every fixture×target pair below compiles cleanly today on all three OSes
    // (the per-target support envelope mirrors the existing corpus tests:
    // CompileFixtureTests for GL/DX breadth, FnaCompileFixtureTests.Sm3Corpus
    // for the FNA SM ≤ 3 envelope).
    // -------------------------------------------------------------------------

    /// <summary>The MGFX baseline corpus (CompileFixtureTests) — GL + DX targets.</summary>
    private static readonly string[] CoreMgfxFixtures =
    [
        "Minimal.fx",
        "textured.fx",
        "cbuffer.fx",
        "multipass.fx",
        "multitechnique.fx",
        "render-states.fx",
        "annotations.fx",
        "platform-macros.fx",
        "basiceffect-mini.fx",
        // Phase 43 writer-fidelity corpus (F1/F2/F9): MGFX-only — SamplerStatesFull
        // uses BorderColor (FNA's runtime throws on it) and AnnotatedTechnique uses
        // technique/pass annotations, so these stay out of the FNA corpus.
        "StateBlendAdditive.fx",
        "StateDepthStencil.fx",
        "StateRasterizer.fx",
        "SamplerStatesFull.fx",
        "AnnotatedTechnique.fx",
    ];

    /// <summary>
    /// The SM ≤ 3 corpus (FnaCompileFixtureTests.Sm3Corpus): the Phase 17/18
    /// rung-4-proven PS-only shaders plus the VS-driven ones — compiled for ALL three
    /// targets, so the GL/DX manifests carry the render-proven corpus too.
    /// </summary>
    private static readonly string[] Sm3Fixtures =
    [
        // PS-only effects
        "Grayscale.fx",
        "Invert.fx",
        "Sepia.fx",
        "Saturate.fx",
        "Pixelated.fx",
        "Scanlines.fx",
        "Fading.fx",
        "Dots.fx",
        "Dissolve.fx",
        "TintShader.fx",
        "BasicShader.fx",
        "BlendShader.fx",
        "ClipShader.fx",
        "ClipShaderNew.fx",
        "ClipShaderSpriteTarget.fx",
        "MultiTexture.fx",
        "MultiTextureOverlay.fx",
        "SimpleLightShader.fx",
        "SpriteAlphaTest.fx",
        "Teleport.fx",
        // VS+PS effects
        "PolygonLight.fx",
        "VertexAndPixel.fx",
        "VsTransformColorTexture.fx",
        "FnaMultiPassStates.fx",
        // Project-owned example shaders (known provenance; legacy SM3 surface)
        "examples/ExBareSamplerTex2D.fx",
        "examples/ExSamplerStateUniform.fx",
        "examples/ExDualTexture.fx",
        "examples/ExLegacyTextureDiscard.fx",
    ];

    private static IEnumerable<string> OpenGLCorpus  => CoreMgfxFixtures.Concat(Sm3Fixtures);
    private static IEnumerable<string> DirectXCorpus => CoreMgfxFixtures.Concat(Sm3Fixtures);
    private static IEnumerable<string> FnaCorpus     => Sm3Fixtures;

    // -------------------------------------------------------------------------
    // The three per-target assertions
    // -------------------------------------------------------------------------

    [DxcFact] // GL front-end is DXC: NuGet-supplied on win/linux, restored dylib on macOS
    public async Task OpenGL_Bytes_MatchCommittedManifest()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertTargetMatchesManifestAsync("OpenGL", OpenGLCorpus, PlatformTarget.OpenGL, cts.Token);
    }

    // FnaFactAttribute is the vkd3d-shader availability gate (FnaTestGate) — exactly
    // the native this target needs: the manifest pins the CROSS-PLATFORM vkd3d backend
    // on every OS, including Windows. The default d3dcompiler_47 oracle is
    // host-dependent by design and must never appear in this manifest.
    [FnaFact]
    public async Task DirectX_Vkd3d_Bytes_MatchCommittedManifest()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertTargetMatchesManifestAsync("DirectX_Vkd3d", DirectXCorpus, PlatformTarget.DirectX, cts.Token);
    }

    [FnaFact]
    public async Task Fna_Bytes_MatchCommittedManifest()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertTargetMatchesManifestAsync("FNA", FnaCorpus, PlatformTarget.Fna, cts.Token);
    }

    // -------------------------------------------------------------------------
    // Normalization guard — SourceFileName must never leak into output bytes
    // (it exists for include resolution + diagnostics only). If this ever fails,
    // the manifest scheme (fixed relative names) is unsound — fix the leak, do
    // not adjust the manifest.
    // -------------------------------------------------------------------------

    [DxcFact]
    public async Task SourceFileName_DoesNotAffect_OutputBytes()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);

        byte[] relative = await CompileAsync("Minimal.fx", PlatformTarget.OpenGL, cts.Token,
            sourceFileNameOverride: "Minimal.fx");
        byte[] absoluteStyle = await CompileAsync("Minimal.fx", PlatformTarget.OpenGL, cts.Token,
            sourceFileNameOverride: "/some/host/specific/path/Minimal.fx");

        absoluteStyle.Should().Equal(relative,
            because: "SourceFileName feeds include resolution and diagnostics only — a " +
                     "host path leaking into the compiled bytes would break cross-host " +
                     "byte identity");
    }

    [FnaFact]
    public async Task SourceFileName_DoesNotAffect_OutputBytes_Vkd3dTargets()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);

        foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.Fna })
        {
            byte[] relative = await CompileAsync("Grayscale.fx", target, cts.Token,
                sourceFileNameOverride: "Grayscale.fx");
            byte[] absoluteStyle = await CompileAsync("Grayscale.fx", target, cts.Token,
                sourceFileNameOverride: "C:\\some\\host\\specific\\path\\Grayscale.fx");

            absoluteStyle.Should().Equal(relative,
                because: $"for target {target}, SourceFileName feeds diagnostics only — a " +
                         "host path leaking into the compiled bytes would break cross-host " +
                         "byte identity");
        }
    }

    // -------------------------------------------------------------------------
    // Implementation
    // -------------------------------------------------------------------------

    private static async Task AssertTargetMatchesManifestAsync(
        string targetKey,
        IEnumerable<string> fixtures,
        PlatformTarget target,
        CancellationToken ct)
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string fx in fixtures)
        {
            byte[] bytes = await CompileAsync(fx, target, ct);
            actual[$"{targetKey}/{fx}"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        if (RegenerateRequested)
        {
            // Regen mode asserts NOTHING — it rewrites the manifest and reports PASS.
            // Guard it so that "PASS" can never be mistaken for verification:
            //   * never in CI (a leaked env var would silently neuter the whole gate);
            //   * only on win-x64 (the manifest's documented canonical host — see the
            //     class doc: hashes are generated on win-x64 and asserted elsewhere).
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                throw new InvalidOperationException(
                    $"{RegenerateEnvVar}=1 is set in CI (GITHUB_ACTIONS=true). Regeneration mode " +
                    "asserts nothing and would turn this byte-identity gate into a fabricated " +
                    "PASS — regenerate locally on win-x64 and commit the reviewed manifest diff.");
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new InvalidOperationException(
                    $"{RegenerateEnvVar}=1 requires win-x64 — the committed manifest's canonical " +
                    $"host. This host is {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); " +
                    "regenerating here would commit per-OS hashes and destroy the cross-host assertion.");

            RegenerateManifestSection(targetKey, actual);
            return;
        }

        IReadOnlyDictionary<string, string> manifest = LoadCommittedManifest();
        var expected = manifest
            .Where(kv => kv.Key.StartsWith(targetKey + "/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // Build an explicit discrepancy report: a hash mismatch on ubuntu/macOS against
        // the win-x64-generated manifest is a REAL cross-host fidelity finding — both
        // hashes must be visible in the failure, never summarized away.
        var discrepancies = new List<string>();
        foreach ((string key, string expectedHash) in expected.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(key, out string? actualHash))
                discrepancies.Add($"MISSING   {key}: manifest has {expectedHash} but the corpus no longer produces this entry");
            else if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                discrepancies.Add($"MISMATCH  {key}: manifest(win-x64)={expectedHash} this-host={actualHash}");
        }
        foreach (string key in actual.Keys.Where(k => !expected.ContainsKey(k)))
            discrepancies.Add($"UNTRACKED {key}: compiled to {actual[key]} but the committed manifest has no entry");

        discrepancies.Should().BeEmpty(
            because: $"every '{targetKey}' output on {RuntimeInformation.OSDescription} " +
                     $"({RuntimeInformation.OSArchitecture}) must be byte-identical to the " +
                     "committed win-x64 manifest (Core Design Constraint 3; Phase 37 tail 1). " +
                     "A mismatch is a REAL per-OS fidelity finding — do not regenerate the " +
                     "manifest per-OS or loosen this to structural equality. If the compiler " +
                     $"output legitimately changed, regenerate via {RegenerateEnvVar}=1 on " +
                     "win-x64 and review the manifest diff. Discrepancies:\n" +
                     string.Join("\n", discrepancies));
    }

    private static async Task<byte[]> CompileAsync(
        string fx,
        PlatformTarget target,
        CancellationToken ct,
        string? sourceFileNameOverride = null)
    {
        // Normalize line endings: git may check fixtures out CRLF on Windows and LF on
        // Linux/macOS. The experiment must feed the compiler IDENTICAL input bytes on
        // every host — EOL flavor is the checkout's doing, not the compiler's.
        string source = (await File.ReadAllTextAsync(TestHelpers.FixturePath(fx), ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var options = new CompilerOptions
        {
            Target = target,
            // Fixed, host-independent relative name (never an absolute host path);
            // used for diagnostics only — none of the corpus fixtures use #include.
            SourceFileName = sourceFileNameOverride ?? fx,
            // Ignored for non-DX targets; for DirectX this selects the cross-platform
            // backend so the same compiler produces the bytes on every OS.
            DxbcBackend = DxbcBackend.Vkd3d,
        };

        var compiler = new EffectCompiler();
        var result = await compiler.CompileAsync(source, options, ct);

        result.IsSuccess.Should().BeTrue(
            because: $"'{fx}' for {target} is in the byte-identity corpus and must compile; " +
                     $"errors: {(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>")}");

        return result.Value.Data;
    }

    // -------------------------------------------------------------------------
    // Manifest I/O
    // -------------------------------------------------------------------------

    private static string CopiedManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "byte-identity", "manifest.json");

    private static IReadOnlyDictionary<string, string> LoadCommittedManifest()
    {
        File.Exists(CopiedManifestPath).Should().BeTrue(
            because: $"the committed manifest must be copied to test output at '{CopiedManifestPath}' " +
                     $"(generate it with {RegenerateEnvVar}=1 on win-x64 if it does not exist yet)");

        return JsonSerializer.Deserialize<SortedDictionary<string, string>>(
                   File.ReadAllText(CopiedManifestPath))
               ?? throw new InvalidOperationException("byte-identity manifest deserialized to null");
    }

    /// <summary>
    /// Regeneration path: replaces this target's section of the SOURCE-TREE manifest
    /// (found by walking up to the repo root), preserving other targets' entries so the
    /// three per-target tests can regenerate independently. LF newlines + trailing
    /// newline keep the committed file byte-stable across regenerating hosts.
    /// </summary>
    private static void RegenerateManifestSection(string targetKey, SortedDictionary<string, string> entries)
    {
        string manifestPath = Path.Combine(
            FindRepoRoot(), "tests", "fixtures", "golden", "byte-identity", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        // Re-wrap in an explicitly Ordinal-sorted dictionary: the deserializer's default
        // comparer is culture-sensitive, which would make the committed key order (and
        // therefore the file bytes) depend on the regenerating host's locale.
        var merged = new SortedDictionary<string, string>(
            (File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifestPath))
                : null) ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);

        foreach (string stale in merged.Keys.Where(k => k.StartsWith(targetKey + "/", StringComparison.Ordinal)).ToList())
            merged.Remove(stale);
        foreach ((string key, string hash) in entries)
            merged[key] = hash;

        string json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (ShadowDusk.slnx) above the test output directory — " +
            "manifest regeneration must run from a repo checkout.");
    }
}

/// <summary>
/// Availability gate for tests whose pipeline starts at DXC. On Windows/Linux the
/// native ships inside the Vortice.Dxc NuGet (always present); on macOS it is our own
/// restored dylib (Phase 37 A, <c>tools/restore</c> → <c>tools/dxc/osx-*/</c>), so the
/// test skips with a clear reason when it has not been restored — the FnaTestGate
/// pattern, applied to DXC.
/// </summary>
public sealed class DxcFactAttribute : FactAttribute
{
    public DxcFactAttribute()
    {
        // SHADOWDUSK_REQUIRE_DXC set (CI) + dylib missing => do NOT skip: the test runs
        // and fails loudly at the DXC load — a restore-infrastructure failure must go
        // red, never quietly skip green. See ShadowDusk.Tests.Shared.NativeRequirement.
        if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                DxcTestGate.DxcAvailable,
                Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
            Skip = DxcTestGate.SkipReason;
    }
}

/// <summary>macOS-only DXC dylib probe; mirrors <see cref="FnaTestGate"/> / DxcLoader.</summary>
internal static class DxcTestGate
{
    internal const string SkipReason =
        "macOS DXC native (libdxcompiler.dylib) not found (restore it via tools/restore — " +
        "see plan/DONE/PHASE-37-cross-platform-native-availability.md, finding A).";

    internal static bool DxcAvailable { get; } = ProbeDxc();

    private static bool ProbeDxc()
    {
        // Vortice.Dxc ships the win-x64/win-arm64/linux-x64 natives transitively.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return true;

        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        foreach (string subdir in new[] { arch, "" })
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, subdir, "libdxcompiler.dylib")))
                return true;

            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "dxc", subdir, "libdxcompiler.dylib")))
                    return true;
            }
        }

        return false;
    }
}
