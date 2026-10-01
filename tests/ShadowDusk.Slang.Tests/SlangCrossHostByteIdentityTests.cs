#nullable enable

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Cross-host byte identity for the real-slangc route (issue #227): every RID's slangc
/// (four separately built upstream binaries) must hand the pipeline the SAME text, and the
/// route must produce the SAME bytes, on every OS. The ShadowDusk.Slang render proof
/// (<c>validation/SlangFullCorpus</c> gate 3, a real WindowsDX <c>Effect</c> load) runs on
/// Windows only; this is what transfers it to Linux and macOS byte for byte, the same way
/// <c>ShadowDusk.Integration.Tests.CrossHostByteIdentityTests</c> does for <c>.fx</c>.
///
/// <para>Three keys per corpus shader, all in
/// <c>tests/fixtures/golden/byte-identity/slang-manifest.json</c>:</para>
/// <list type="bullet">
///   <item><c>AssembledFx/&lt;file&gt;</c>: the <c>.fx</c> text <see cref="SlangCompiler"/>
///   hands downstream for OpenGL (slangc's own HLSL emission, merged and wrapped), so a
///   per-RID slangc divergence is caught at its source, not just in the final bytes;</item>
///   <item><c>OpenGL/&lt;file&gt;</c> and <c>DirectX_Vkd3d/&lt;file&gt;</c>: SHA-256 of the
///   compiled <c>.mgfx</c>. Both downstream pipelines are already proven host-independent
///   by the <c>.fx</c> manifest; DX12/Vulkan are not pinned there, so not here either.</item>
/// </list>
///
/// <para><b>Regenerating:</b> <c>SHADOWDUSK_REGENERATE_BYTE_MANIFEST=1</c> on any bundled
/// host, never in CI. First generated on osx-arm64 (the one host the issue-#227 work could
/// run); a mismatch on another OS is a REAL per-RID slangc finding, never a reason for a
/// per-OS manifest.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangCrossHostByteIdentityTests
{
    private const string RegenerateEnvVar = "SHADOWDUSK_REGENERATE_BYTE_MANIFEST";

    private static readonly string RepoRoot = FindRepoRoot();
    private static string ManifestPath =>
        Path.Combine(RepoRoot, "tests", "fixtures", "golden", "byte-identity", "slang-manifest.json");

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Task.FromResult(Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, [])));
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, []));
        }
    }

    private static IEnumerable<(string Key, string Path)> Corpus()
    {
        string shipped = Path.Combine(RepoRoot, "tests", "fixtures", "shaders", "slang");
        foreach (string f in Directory.GetFiles(shipped, "*.slang").OrderBy(x => x, StringComparer.Ordinal))
            yield return ($"slang/{Path.GetFileName(f)}", f);

        string probe = Path.Combine(RepoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders");
        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang", "GenericsProbe.slang" })
            yield return ($"phase65-probe/{name}", Path.Combine(probe, name));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public void SlangRoute_Bytes_MatchCommittedManifest()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach ((string key, string path) in Corpus())
        {
            // EOL normalized: the checkout's line endings are not the compiler's doing.
            string source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            string name = Path.GetFileName(path);

            var capture = new CapturingCompiler();
            var assembled = new SlangCompiler(capture).Compile(
                source, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = name });
            if (assembled.IsFailure || capture.Captured is null)
            {
                failures.Add($"{key} (assemble): {Errors(assembled)}");
                continue;
            }
            actual[$"AssembledFx/{key}"] = Sha(Encoding.UTF8.GetBytes(capture.Captured));

            foreach ((string targetKey, PlatformTarget target) in new[]
                     { ("OpenGL", PlatformTarget.OpenGL), ("DirectX_Vkd3d", PlatformTarget.DirectX) })
            {
                var result = new SlangCompiler().Compile(source, new CompilerOptions
                {
                    Target = target,
                    SourceFileName = name,
                    DxbcBackend = DxbcBackend.Vkd3d,
                });
                if (result.IsFailure)
                {
                    failures.Add($"{key} ({targetKey}): {Errors(result)}");
                    continue;
                }
                actual[$"{targetKey}/{key}"] = Sha(result.Value.Data);
            }
        }

        failures.ShouldBeEmpty("every corpus shader must compile on this host:\n" + string.Join("\n", failures));
        actual.Count.ShouldBe(21 * 3);

        if (Environment.GetEnvironmentVariable(RegenerateEnvVar) == "1")
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                throw new InvalidOperationException(
                    $"{RegenerateEnvVar}=1 in CI would turn this gate into a fabricated PASS; regenerate locally.");
            File.WriteAllText(ManifestPath,
                JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");
            return;
        }

        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(ManifestPath))
            ?? throw new InvalidOperationException("slang byte-identity manifest deserialized to null");

        var discrepancies = new List<string>();
        foreach ((string key, string hash) in expected)
        {
            if (!actual.TryGetValue(key, out string? got))
                discrepancies.Add($"MISSING   {key}: manifest has {hash}, this host produced no entry");
            else if (got != hash)
                discrepancies.Add($"MISMATCH  {key}: manifest={hash} this-host={got}");
        }
        foreach (string key in actual.Keys.Where(k => !expected.ContainsKey(k)))
            discrepancies.Add($"UNTRACKED {key}: {actual[key]}");

        discrepancies.ShouldBeEmpty(
            $"the real-slangc route on {RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.OSDescription}) " +
            "must produce the committed bytes. A mismatch is a REAL per-RID slangc (or pipeline) finding: " +
            "report it with both hashes, never regenerate per OS. Discrepancies:\n" + string.Join("\n", discrepancies));
    }

    private static string Errors(Result<CompiledShader, ShaderError[]> r) =>
        r.IsFailure ? string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }
}
