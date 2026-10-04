#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShadowDusk.Tests.Shared;

/// <summary>
/// The cross-ARCHITECTURE byte-identity harness (issue #286): one CI job compiles a corpus on
/// win-x64 and writes what every compile produced; a second job compiles the same corpus as a
/// native win-arm64 process and must produce exactly the same thing. Unlike the committed
/// <c>manifest.json</c> (OpenGL, DirectX vkd3d and FNA only), this covers every target a host
/// can run, including DirectX 12 and Vulkan, whose MGFX v11 output stores a source name and so
/// is not pinned in a committed cross-OS manifest. Nothing is committed: the x64 job's file is a
/// workflow artifact, so the comparison is always against the same commit's win-x64 output.
///
/// <para>Each entry is <c>sha256:&lt;hex&gt;</c> for a successful compile, or
/// <c>error:</c> plus the diagnostics for a refused one, so an arm64 host that refuses a shader
/// x64 compiles (or the reverse) is a mismatch too, never a silently shorter corpus.</para>
///
/// <para>Source-linked into <c>ShadowDusk.Slang.Tests</c> (the <c>NativeRequirement</c>
/// pattern), so the Slang route uses the same write/compare rules.</para>
/// </summary>
internal static class ArchitectureManifest
{
    /// <summary>Directory to WRITE this host's manifests into (the win-x64 job).</summary>
    internal const string WriteEnvVar = "SHADOWDUSK_ARCH_MANIFEST_WRITE";

    /// <summary>Directory holding the win-x64 manifests to COMPARE against (the win-arm64 job).</summary>
    internal const string ExpectEnvVar = "SHADOWDUSK_ARCH_MANIFEST_EXPECT";

    /// <summary>The <see cref="Architecture"/> name the process must be running as, when set.</summary>
    internal const string ExpectedArchitectureEnvVar = "SHADOWDUSK_EXPECT_PROCESS_ARCH";

    internal static string? WriteDirectory => NonEmpty(Environment.GetEnvironmentVariable(WriteEnvVar));
    internal static string? ExpectDirectory => NonEmpty(Environment.GetEnvironmentVariable(ExpectEnvVar));

    /// <summary>Skip reason for a test that only makes sense inside the two-job comparison.</summary>
    internal static string? SkipReason =>
        WriteDirectory is null && ExpectDirectory is null
            ? $"cross-architecture manifest run only: set {WriteEnvVar} (win-x64 job) or {ExpectEnvVar} " +
              "(win-arm64 job); see .github/workflows/win-arm64.yml (issue #286)"
            : null;

    internal static string Hash(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Writes <paramref name="actual"/> as <c>&lt;section&gt;.json</c> when <see cref="WriteEnvVar"/>
    /// is set, and returns every discrepancy against the expected <c>&lt;section&gt;.json</c> when
    /// <see cref="ExpectEnvVar"/> is set (empty when it matches, or when only writing).
    /// </summary>
    internal static IReadOnlyList<string> WriteOrCompare(string section, IReadOnlyDictionary<string, string> actual)
    {
        var sorted = new SortedDictionary<string, string>(actual.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);

        if (WriteDirectory is { } writeDir)
        {
            Directory.CreateDirectory(writeDir);
            string json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(writeDir, section + ".json"), json.ReplaceLineEndings("\n") + "\n",
                new UTF8Encoding(false));
        }

        if (ExpectDirectory is not { } expectDir)
            return [];

        string expectedPath = Path.Combine(expectDir, section + ".json");
        if (!File.Exists(expectedPath))
            return [$"NO-MANIFEST {expectedPath} does not exist (did the win-x64 job upload it?)"];

        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(expectedPath))
            ?? throw new InvalidOperationException($"{expectedPath} deserialized to null");

        var discrepancies = new List<string>();
        foreach ((string key, string want) in expected.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!sorted.TryGetValue(key, out string? got))
                discrepancies.Add($"MISSING   {key}: win-x64={want}, this host produced no entry");
            else if (!string.Equals(got, want, StringComparison.Ordinal))
                discrepancies.Add($"MISMATCH  {key}: win-x64={want} this-host={got}");
        }
        foreach (string key in sorted.Keys.Where(k => !expected.ContainsKey(k)))
            discrepancies.Add($"UNTRACKED {key}: {sorted[key]} (win-x64 produced no entry)");

        return discrepancies;
    }

    /// <summary>One line for the test log: how many entries of each key prefix compiled vs were refused.</summary>
    internal static string Summary(IReadOnlyDictionary<string, string> actual) =>
        string.Join("; ", actual
            .GroupBy(kv => kv.Key[..Math.Max(0, kv.Key.IndexOf('/'))], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {g.Count(kv => kv.Value.StartsWith("sha256:", StringComparison.Ordinal))} compiled, " +
                         $"{g.Count(kv => !kv.Value.StartsWith("sha256:", StringComparison.Ordinal))} refused"));

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
