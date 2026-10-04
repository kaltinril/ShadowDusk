#nullable enable

using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Core;
using ShadowDusk.Tests.Shared;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #286: the real-slangc route on a native win-arm64 process must produce win-x64's
/// exact text and bytes. The committed <c>slang-manifest.json</c> pins OpenGL and DirectX
/// (vkd3d), and win-arm64 cannot run vkd3d, so this compares the targets win-arm64 does run
/// (OpenGL, Vulkan, DirectX 12) against the same commit's win-x64 output instead, through
/// <see cref="ArchitectureManifest"/> (<c>.github/workflows/win-arm64.yml</c>). Skips unless
/// that run is configured.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangArchitectureByteIdentityTests(ITestOutputHelper output)
{
    private static readonly (string Key, PlatformTarget Target)[] Targets =
    [
        ("OpenGL", PlatformTarget.OpenGL),
        ("Vulkan", PlatformTarget.Vulkan),
        ("DirectX12", PlatformTarget.DirectX12),
    ];

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, []));
        }
    }

    [SlangArchitectureManifestFact]
    public void SlangRoute_GlVulkanDx12_MatchesWinX64()
    {
        output.WriteLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture} " +
                         $"RuntimeIdentifier={RuntimeInformation.RuntimeIdentifier} slangc={SlangToolPath.Resolve() ?? "<none>"}");
        if (Environment.GetEnvironmentVariable(ArchitectureManifest.ExpectedArchitectureEnvVar) is { Length: > 0 } arch)
            RuntimeInformation.ProcessArchitecture.ToString().ShouldBe(arch);

        string root = FindRepoRoot();
        var corpus = new List<(string Key, string Path)>();
        string shipped = Path.Combine(root, "tests", "fixtures", "shaders", "slang");
        foreach (string f in Directory.GetFiles(shipped, "*.slang").OrderBy(x => x, StringComparer.Ordinal))
            corpus.Add(($"slang/{Path.GetFileName(f)}", f));
        string probe = Path.Combine(root, "plan", "PHASE-65-appendix", "slang-probe", "shaders");
        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang", "GenericsProbe.slang" })
            corpus.Add(($"phase65-probe/{name}", Path.Combine(probe, name)));

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string path) in corpus)
        {
            string source = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            string name = Path.GetFileName(path);
            foreach ((string targetKey, PlatformTarget target) in Targets)
            {
                var options = new CompilerOptions { Target = target, SourceFileName = name };

                // slangc's own emission, merged and wrapped: a per-architecture slangc
                // divergence shows here at its source, not only in the final bytes.
                var capture = new CapturingCompiler();
                var assembled = new SlangCompiler(capture).Compile(source, options);
                actual[$"AssembledFx-{targetKey}/{key}"] = assembled.IsSuccess && capture.Captured is not null
                    ? ArchitectureManifest.Hash(Encoding.UTF8.GetBytes(capture.Captured))
                    : "error:" + Errors(assembled);

                var compiled = new SlangCompiler().Compile(source, options);
                actual[$"{targetKey}/{key}"] = compiled.IsSuccess
                    ? ArchitectureManifest.Hash(compiled.Value.Data)
                    : "error:" + Errors(compiled);
            }
        }

        output.WriteLine(ArchitectureManifest.Summary(actual));
        foreach ((string targetKey, _) in Targets)
        {
            actual.Count(kv => kv.Key.StartsWith(targetKey + "/", StringComparison.Ordinal)
                               && kv.Value.StartsWith("sha256:", StringComparison.Ordinal))
                .ShouldBeGreaterThan(10, $"most of the Slang corpus must compile for {targetKey}, or a match proves nothing");
        }

        IReadOnlyList<string> discrepancies = ArchitectureManifest.WriteOrCompare("slang-gl-vulkan-dx12", actual);
        discrepancies.ShouldBeEmpty(
            $"the real-slangc route on {RuntimeInformation.RuntimeIdentifier} must produce win-x64's exact text and " +
            "bytes. A mismatch is a REAL per-architecture slangc (or pipeline) finding. Discrepancies:\n" +
            string.Join("\n", discrepancies));
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

/// <summary>Skips unless the cross-architecture manifest run is configured (issue #286).</summary>
public sealed class SlangArchitectureManifestFactAttribute : FactAttribute
{
    public SlangArchitectureManifestFactAttribute()
    {
        if (ArchitectureManifest.SkipReason is { } reason)
            Skip = reason;
    }
}
