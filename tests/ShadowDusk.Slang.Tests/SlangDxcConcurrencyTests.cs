#nullable enable

using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #256: <see cref="SlangCompiler"/> starts a slangc process per entry point (a
/// <c>fork()</c> on macOS, <c>vfork()</c> on Linux) while other threads run DXC in process, the
/// exact mix that deadlocked macOS libc before <c>DxcForkGate</c> (DXC's <c>setlocale</c>
/// against <c>fork()</c>'s atfork locking). This runs Slang compiles (slangc spawn, then the
/// in-process DXC pipeline) in parallel with ordinary <c>.fx</c> compiles, and fails on any
/// error or on a hang past the watchdog. Runs on every integration lane: ubuntu, macOS and
/// Windows all restore slangc.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangDxcConcurrencyTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }

    [Fact]
    public async Task SlangCompiles_InParallelWithFxCompiles_AllSucceedWithoutDeadlock()
    {
        const int workersPerKind = 6;
        const int iterations = 4;

        string slangDir = Path.Combine(RepoRoot, "tests", "fixtures", "shaders", "slang");
        string[] slangSources = new[] { "Desaturate.slang", "Invert.slang", "Plasma.slang" }
            .Select(f => File.ReadAllText(Path.Combine(slangDir, f)))
            .ToArray();
        string fxDir = Path.Combine(RepoRoot, "tests", "fixtures", "shaders");
        string[] fxSources = new[] { "Grayscale.fx", "Invert.fx", "BasicShader.fx" }
            .Select(f => File.ReadAllText(Path.Combine(fxDir, f)))
            .ToArray();

        int slangDone = 0, fxDone = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        static PlatformTarget TargetFor(int w, int i) => ((w + i) & 1) == 0 ? PlatformTarget.OpenGL : PlatformTarget.DirectX;

        IEnumerable<Task> slang = Enumerable.Range(0, workersPerKind).Select(w => Task.Run(async () =>
        {
            var compiler = new SlangCompiler();
            for (int i = 0; i < iterations; i++)
            {
                int pick = (w + i) % slangSources.Length;
                var options = new CompilerOptions { Target = TargetFor(w, i), SourceFileName = $"slang{pick}.slang" };
                var result = await compiler.CompileAsync(slangSources[pick], options);
                if (result.IsFailure)
                    failures.Enqueue($"slang {options.SourceFileName} {options.Target}: " + string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)));
                else
                    Interlocked.Increment(ref slangDone);
            }
        }));

        IEnumerable<Task> fx = Enumerable.Range(0, workersPerKind).Select(w => Task.Run(async () =>
        {
            var compiler = new EffectCompiler();
            for (int i = 0; i < iterations; i++)
            {
                int pick = (w + i) % fxSources.Length;
                var options = new CompilerOptions { Target = TargetFor(w, i), SourceFileName = $"fx{pick}.fx" };
                var result = await compiler.CompileAsync(fxSources[pick], options);
                if (result.IsFailure)
                    failures.Enqueue($"fx {options.SourceFileName} {options.Target}: " + string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)));
                else
                    Interlocked.Increment(ref fxDone);
            }
        }));

        Task all = Task.WhenAll(slang.Concat(fx).ToArray());
        try
        {
            // A native deadlock never returns; this turns it into a failure (the integration
            // job's --blame-hang then dumps the stuck host's threads).
            await all.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException(
                $"Slang + .fx compiles hung past 2 min (slang done {slangDone}, fx done {fxDone}): " +
                "likely a native deadlock between slangc's process spawn and in-process DXC.");
        }

        failures.ShouldBeEmpty();
        slangDone.ShouldBe(workersPerKind * iterations);
        fxDone.ShouldBe(workersPerKind * iterations);
    }
}
