#nullable enable

using System.Runtime.InteropServices;
using System.Text.Json;
using Shouldly;
using ShadowDusk.Probes;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The OpenGL corpus's INTERMEDIATES, pinned like its output (issue #304 follow-up): for every
/// OpenGL entry of <c>manifest.json</c>, the SPIR-V DXC returned and the GLSL SPIRV-Cross returned
/// must hash to the committed <c>intermediates-manifest.json</c>, and the <c>.mgfx</c> built with
/// the managed <c>SpirvReflector</c> (Android's default) must be the <c>manifest.json</c> bytes.
///
/// <para>Why it exists: the desktop and the Android device run DIFFERENT native builds of DXC and
/// SPIRV-Cross (Vortice / Silk.NET packages on the desktop, our NDK builds on Android). The
/// Android emulator lane compiles this same corpus ON the device through the same
/// <see cref="OpenGlCorpusProbe"/> and checks it against these manifests, so a native that
/// transpiles differently is named per stage (SPIR-V = DXC, GLSL = SPIRV-Cross). This class is the
/// desktop half: it proves the manifest is what every desktop OS produces.</para>
///
/// <para>Regenerate after a reviewed compiler change with <c>SHADOWDUSK_REGENERATE_BYTE_MANIFEST=1</c>
/// on win-x64, exactly as <see cref="CrossHostByteIdentityTests"/>.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Fidelity")]
public sealed class OpenGlIntermediatesByteIdentityTests
{
    private const string RegenerateEnvVar = "SHADOWDUSK_REGENERATE_BYTE_MANIFEST";

    private static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "byte-identity");

    [DxcFact]
    public async Task OpenGL_Intermediates_MatchCommittedManifest()
    {
        using var cts = new CancellationTokenSource(TestBudget.Compile);
        string manifestJson = await File.ReadAllTextAsync(Path.Combine(Dir, "manifest.json"), cts.Token);
        var mgfx = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson)!;
        IReadOnlyList<string> fixtures = OpenGlCorpusProbe.OpenGlFixtures(manifestJson);
        fixtures.Count.ShouldBeGreaterThan(40, "the OpenGL corpus of manifest.json");

        var actual = new Dictionary<string, OpenGlCorpusProbe.Hashes>(StringComparer.Ordinal);
        foreach (string fx in fixtures)
        {
            string source = await File.ReadAllTextAsync(TestHelpers.FixturePath(fx), cts.Token);
            var result = await OpenGlCorpusProbe.CompileAsync(fx, source, cts.Token);
            result.IsSuccess.ShouldBeTrue($"{fx}: {(result.IsFailure ? result.Error : "")}");
            result.Value.SpirvCount.ShouldBeGreaterThan(0, $"{fx}: no SPIR-V was recorded");
            result.Value.GlslCount.ShouldBeGreaterThan(0, $"{fx}: no GLSL was recorded");
            actual[fx] = result.Value;
        }

        string path = Path.Combine(Dir, "intermediates-manifest.json");
        if (Environment.GetEnvironmentVariable(RegenerateEnvVar) == "1")
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                throw new InvalidOperationException($"{RegenerateEnvVar}=1 is set in CI; regeneration asserts nothing.");
            if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
                throw new InvalidOperationException($"{RegenerateEnvVar}=1 requires win-x64, the manifest's canonical host.");

            string source = Path.Combine(FindRepoRoot(), "tests", "fixtures", "golden", "byte-identity", "intermediates-manifest.json");
            await File.WriteAllTextAsync(source, OpenGlCorpusProbe.ToManifestJson(actual), cts.Token);
            return;
        }

        var expected = OpenGlCorpusProbe.ReadIntermediates(await File.ReadAllTextAsync(path, cts.Token));
        var discrepancies = new List<string>();
        foreach ((string fx, OpenGlCorpusProbe.Hashes h) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string key = OpenGlCorpusProbe.Prefix + fx;
            if (!string.Equals(h.Mgfx, mgfx[key], StringComparison.Ordinal))
                discrepancies.Add($"MGFX   {key}: manifest.json={mgfx[key]} SpirvReflector={h.Mgfx}");
            if (!expected.TryGetValue(key, out var e))
            {
                discrepancies.Add($"UNTRACKED {key}: not in intermediates-manifest.json");
                continue;
            }

            if (!string.Equals(h.Spirv, e.Spirv, StringComparison.Ordinal))
                discrepancies.Add($"SPIRV  {key}: manifest={e.Spirv} this-host={h.Spirv}");
            if (!string.Equals(h.Glsl, e.Glsl, StringComparison.Ordinal))
                discrepancies.Add($"GLSL   {key}: manifest={e.Glsl} this-host={h.Glsl}");
        }

        foreach (string key in expected.Keys.Where(k => !actual.ContainsKey(k.Substring(OpenGlCorpusProbe.Prefix.Length))))
            discrepancies.Add($"MISSING {key}: in intermediates-manifest.json but not in the corpus");

        discrepancies.ShouldBeEmpty(
            $"the OpenGL corpus's SPIR-V and GLSL on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) " +
            "must be byte-identical to the committed win-x64 intermediates manifest, and the SpirvReflector .mgfx to " +
            "manifest.json. A mismatch is a real per-OS native difference; never regenerate per OS. Discrepancies:\n" +
            string.Join("\n", discrepancies));
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
