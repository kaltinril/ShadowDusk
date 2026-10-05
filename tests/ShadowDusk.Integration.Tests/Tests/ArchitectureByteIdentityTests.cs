#nullable enable

using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #286: does the core pipeline produce win-x64's exact bytes as a native win-arm64
/// process? Driven by <c>.github/workflows/win-arm64.yml</c>: the win-x64 job runs this class
/// with <see cref="ArchitectureManifest.WriteEnvVar"/> set, the windows-11-arm job with
/// <see cref="ArchitectureManifest.ExpectEnvVar"/> (and
/// <see cref="ArchitectureManifest.ExpectedArchitectureEnvVar"/> = <c>Arm64</c>, so an x64
/// process under emulation can never pass as an arm64 measurement). Without either variable
/// every test here skips, so the normal suite is unaffected.
///
/// <para>The corpus is every <c>.fx</c> under <c>tests/fixtures/shaders</c>, compiled for the
/// targets win-arm64 can run (OpenGL, Vulkan, DirectX 12: DXC and SPIRV-Cross ship win-arm64
/// natives). DirectX 11 and FNA need vkd3d-shader, which ShadowDusk ships for no win-arm64, so
/// there the contract is a registered <c>SD0211</c>, never a crash
/// (<see cref="DirectX11AndFna_WithoutVkd3d_AreSd0211_NeverACrash"/>).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ArchitectureByteIdentityTests(ITestOutputHelper output)
{
    private static readonly (string Key, PlatformTarget Target)[] Targets =
    [
        ("OpenGL", PlatformTarget.OpenGL),
        ("Vulkan", PlatformTarget.Vulkan),
        ("DirectX12", PlatformTarget.DirectX12),
    ];

    private static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "shaders");

    [ArchitectureManifestFact]
    public void ProcessArchitecture_IsTheExpectedOne()
    {
        string? expected = Environment.GetEnvironmentVariable(ArchitectureManifest.ExpectedArchitectureEnvVar);
        output.WriteLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture} " +
                         $"OSArchitecture={RuntimeInformation.OSArchitecture} " +
                         $"RuntimeIdentifier={RuntimeInformation.RuntimeIdentifier} " +
                         $"Framework={RuntimeInformation.FrameworkDescription}");
        if (string.IsNullOrWhiteSpace(expected))
            return;

        RuntimeInformation.ProcessArchitecture.ToString().ShouldBe(expected,
            "the cross-architecture measurement must run in a NATIVE process of the expected architecture " +
            "(an x64 test host under emulation on an arm64 runner would measure x64 again)");
    }

    [ArchitectureManifestFact]
    public async Task FxCorpus_GlVulkanDx12_MatchesWinX64()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        string root = FixturesRoot;

        string[] fixtures = Directory.GetFiles(root, "*.fx", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        fixtures.Length.ShouldBeGreaterThan(100, "the whole fixture corpus must be copied to the test output");

        var compiler = new EffectCompiler();
        foreach (string fx in fixtures)
        {
            string path = Path.Combine(root, fx);
            string source = (await File.ReadAllTextAsync(path, cts.Token)).Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach ((string key, PlatformTarget target) in Targets)
            {
                var options = new CompilerOptions
                {
                    Target = target,
                    // Absolute so #include resolves beside the fixture; the embedded (MGFX v11)
                    // name is the fixed relative one, so the checkout path never reaches the bytes.
                    SourceFileName = path,
                    EmbeddedSourceFileName = fx,
                };
                actual[$"{key}/{fx}"] = await CompileEntryAsync(compiler, source, options, root, cts.Token);
            }
        }

        output.WriteLine(ArchitectureManifest.Summary(actual));
        foreach ((string key, _) in Targets)
        {
            actual.Count(kv => kv.Key.StartsWith(key + "/", StringComparison.Ordinal)
                               && kv.Value.StartsWith("sha256:", StringComparison.Ordinal))
                .ShouldBeGreaterThan(50, $"most of the corpus must compile for {key}, or a match proves nothing");
        }

        IReadOnlyList<string> discrepancies = ArchitectureManifest.WriteOrCompare("fx-gl-vulkan-dx12", actual);
        discrepancies.ShouldBeEmpty(
            $"every OpenGL / Vulkan / DirectX 12 compile on {RuntimeInformation.RuntimeIdentifier} " +
            $"({RuntimeInformation.ProcessArchitecture}) must produce win-x64's exact result. A mismatch is a REAL " +
            "per-architecture native finding (DXC or SPIRV-Cross): report it with both values. Discrepancies:\n" +
            string.Join("\n", discrepancies));
    }

    /// <summary>
    /// The consumer-visible contract where vkd3d-shader has no build for the process RID
    /// (win-arm64 today): the default DirectX 11 backend and the FNA target fail with the
    /// registered <c>SD0211</c>, never an exception. Runs only on such a host (skips elsewhere,
    /// unless the run expects an Arm64 process, where it must run and fails on any other).
    /// </summary>
    [WinArm64Fact]
    public async Task DirectX11AndFna_WithoutVkd3d_AreSd0211_NeverACrash()
    {
        (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64).ShouldBeTrue(
            $"this measurement needs a native win-arm64 process, got {RuntimeInformation.RuntimeIdentifier} " +
            $"({RuntimeInformation.ProcessArchitecture})");

        string source = (await File.ReadAllTextAsync(Path.Combine(FixturesRoot, "Grayscale.fx")))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (PlatformTarget target in new[] { PlatformTarget.DirectX, PlatformTarget.Fna })
        {
            var result = await new EffectCompiler().CompileAsync(source,
                new CompilerOptions { Target = target, SourceFileName = "Grayscale.fx" });

            result.IsFailure.ShouldBeTrue($"{target} cannot compile on win-arm64 (no vkd3d-shader build)");
            output.WriteLine($"{target}: {string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))}");
            result.Error.ShouldContain(e => e.Code == "SD0211",
                $"{target} on win-arm64 must report the registered SD0211, got: " +
                string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")));
        }

        // Informational only: the opt-in d3dcompiler_47 oracle backend (Windows ships an arm64
        // build of the DLL). Not a supported path for this question; logged for the record.
        try
        {
            var oracle = await new EffectCompiler().CompileAsync(source, new CompilerOptions
            {
                Target = PlatformTarget.DirectX,
                SourceFileName = "Grayscale.fx",
                DxbcBackend = DxbcBackend.D3DCompiler,
            });
            output.WriteLine(oracle.IsSuccess
                ? $"DirectX (DxbcBackend.D3DCompiler, opt-in oracle): compiled, {oracle.Value.Data.Length} bytes"
                : $"DirectX (DxbcBackend.D3DCompiler, opt-in oracle): {string.Join(" | ", oracle.Error.Select(e => $"{e.Code}: {e.Message}"))}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"DirectX (DxbcBackend.D3DCompiler, opt-in oracle): threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> CompileEntryAsync(
        EffectCompiler compiler, string source, CompilerOptions options, string root, CancellationToken ct)
    {
        try
        {
            var result = await compiler.CompileAsync(source, options, ct);
            if (result.IsSuccess)
                return ArchitectureManifest.Hash(result.Value.Data);

            // The checkout path differs between runners; diagnostics spell it with either separator.
            string rootFwd = root.Replace('\\', '/').TrimEnd('/') + "/";
            string Strip(string s) => s.Replace('\\', '/').Replace(rootFwd, "", StringComparison.OrdinalIgnoreCase);
            return "error:" + string.Join(" | ", result.Error.Select(e =>
                $"{Strip(e.File)}({e.Line},{e.Column}) {e.Code}: {Strip(e.Message)}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throw is never an accepted outcome; recording it (instead of failing at the first
            // one) makes the whole corpus's picture visible in one run.
            return $"exception:{ex.GetType().FullName}: {ex.Message}";
        }
    }
}

/// <summary>
/// Skips unless this is a native win-arm64 process. Never skips when the run sets
/// <see cref="ArchitectureManifest.ExpectedArchitectureEnvVar"/> to <c>Arm64</c>: there a wrong
/// process must fail the test, not hide it as a skip (issue #286).
/// </summary>
public sealed class WinArm64FactAttribute : FactAttribute
{
    public WinArm64FactAttribute()
    {
        bool winArm64 = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        bool arm64Expected = string.Equals(
            Environment.GetEnvironmentVariable(ArchitectureManifest.ExpectedArchitectureEnvVar), "Arm64", StringComparison.Ordinal);
        if (!winArm64 && !arm64Expected)
            Skip = $"not a win-arm64 process ({RuntimeInformation.RuntimeIdentifier}); vkd3d-shader ships here (issue #286)";
    }
}

/// <summary>Skips unless the cross-architecture manifest run is configured (issue #286).</summary>
public sealed class ArchitectureManifestFactAttribute : FactAttribute
{
    public ArchitectureManifestFactAttribute()
    {
        if (ArchitectureManifest.SkipReason is { } reason)
            Skip = reason;
    }
}
