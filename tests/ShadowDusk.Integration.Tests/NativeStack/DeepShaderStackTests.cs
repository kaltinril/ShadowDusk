#nullable enable

using System.Diagnostics;
using System.Globalization;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Integration.Tests.Dxc;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.NativeStack;

/// <summary>
/// Issue #306: a valid but extremely deep shader overflowed the native stack inside the
/// compiler (DXC, and vkd3d for a call chain) on the caller's thread and killed the host
/// process with <c>0xC00000FD</c>, no diagnostic. The native calls now run on
/// <see cref="NativeCompileStack"/>'s 64 MB workers. Shapes that die cannot be compiled in the
/// test host (a stack overflow takes the process down), so each one runs in a child process
/// and the exit code is the verdict.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeepShaderStackTests
{
    private readonly ITestOutputHelper _output;

    public DeepShaderStackTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Red before, green after: each row died with a stack overflow on the 1.5 MB thread stack
    /// the .NET host gives a Windows thread (measured ceilings: 2,167 additive terms, 1,619
    /// else-if branches on DirectX 12, between 3,200 and 6,400 chained calls through vkd3d),
    /// and the additive chain's 16,000 terms also exceed the usual 8 MB Linux/macOS stack.
    /// Every row compiles on the 64 MB worker on every OS.
    /// </summary>
    [Theory]
    [InlineData("add", 16000, PlatformTarget.OpenGL, "worker")]       // DXC SPIR-V + DXIL reflection + SPIRV-Cross, CompileAsync
    [InlineData("add", 16000, PlatformTarget.OpenGL, "worker-sync")]  // the same through the synchronous Compile (per-call handoff)
    [InlineData("add", 16000, PlatformTarget.Vulkan, "worker")]       // DXC SPIR-V
    [InlineData("elseif", 3200, PlatformTarget.DirectX12, "worker")]  // DXC DXIL (no SPIR-V nesting limit here)
    [InlineData("calls", 6400, PlatformTarget.DirectX, "worker-sync")] // vkd3d, synchronous
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_DeepShader_CompilesOnTheLargeStackWorker(string shape, int depth, PlatformTarget target, string mode)
    {
        (int exitCode, string output) = await RunProbeAsync(shape, depth, target, mode, TimeSpan.FromMinutes(4));

        exitCode.ShouldBe(0, $"the {depth}-deep {shape} shader did not compile (a signal/0xC00000FD exit is the stack overflow back):\n{output}");
        output.ShouldContain(DeepShaderProbe.VerdictPrefix + "OK", Case.Sensitive);
    }

    /// <summary>
    /// The positive control: with the worker switched off (the pre-fix behaviour) the same kind
    /// of shader at a depth beyond every host's default thread stack must still kill the child,
    /// so a regression that quietly stops routing the call to the worker fails the theory above
    /// rather than passing because the compiler got cheaper. A clean diagnostic (exit 2) or a
    /// success would mean the premise changed and the depths need re-measuring.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task Compile_DeepShaderOnTheCallersStack_StillOverflows_ProvingTheProbeCanDetectIt()
    {
        (int exitCode, string output) = await RunProbeAsync("add", 100_000, PlatformTarget.OpenGL, "inline", TimeSpan.FromMinutes(4));

        output.ShouldContain(DeepShaderProbe.VerdictPrefix + "START", Case.Sensitive);
        output.ShouldNotContain(DeepShaderProbe.VerdictPrefix + "OK", Case.Sensitive);
        exitCode.ShouldNotBe(0, "a 100,000-term chain compiled on the default thread stack: the control no longer overflows\n" + output);
        exitCode.ShouldNotBe(2, "DXC now refuses the 100,000-term chain cleanly; pick a deeper or different control shape\n" + output);
    }

    /// <summary>
    /// Concurrent compiles stay concurrent: the workers are a pool, not one serialized thread
    /// (project_decisions: compiles stay parallel). Eight deep compiles through the public API
    /// at once all succeed, produce identical bytes, and need more than one worker.
    /// </summary>
    /// <remarks>
    /// The eight callers are dedicated threads, not thread-pool tasks: a barrier across pool
    /// tasks blocks the pool threads it waits on, and a starved CI runner can take longer than
    /// the barrier's timeout to supply the eighth one (seen on windows-latest). Dedicated threads
    /// start at once, and the synchronous <c>Compile</c> keeps the pool out of the handoff too,
    /// so the only thing measured is the worker pool.
    /// </remarks>
    [Fact]
    [Trait("Platform", "OpenGL")]
    public void Compile_ParallelDeepCompiles_AllSucceedOnSeparateWorkers()
    {
        const int parallel = 8;
        string fx = DeepShaderProbe.Effect("add", 1600);
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "deep.fx" };
        var compiler = new EffectCompiler();

        int workersBefore = NativeCompileStack.WorkersStarted;
        using var start = new Barrier(parallel);
        var results = new Result<CompiledShader, ShaderError[]>[parallel];
        var failures = new Exception?[parallel];
        Thread[] callers = Enumerable.Range(0, parallel)
            .Select(i => new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(TimeSpan.FromSeconds(30)).ShouldBeTrue("the parallel compiles never all started");
                    results[i] = compiler.Compile(fx, options);
                }
                catch (Exception ex)
                {
                    failures[i] = ex;
                }
            }) { IsBackground = true, Name = $"deep-compile-{i}" })
            .ToArray();

        foreach (Thread caller in callers) caller.Start();
        foreach (Thread caller in callers)
            caller.Join(TimeSpan.FromMinutes(4)).ShouldBeTrue($"{caller.Name} did not finish");

        failures.Where(f => f is not null).ToArray().ShouldBeEmpty();
        foreach (var result in results)
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].FxcFormattedMessage : "");
        results.Select(r => Convert.ToHexString(r.Value.Data)).Distinct().Count().ShouldBe(1, "parallel compiles of one source produced different bytes");

        if (Environment.ProcessorCount > 1)
        {
            (NativeCompileStack.WorkersStarted - workersBefore).ShouldBeGreaterThanOrEqualTo(2,
                "eight simultaneous compiles ran on one worker: the native calls are being serialized");
        }
    }

    /// <summary>
    /// The thread a native compiler runs on does not change its bytes: the same effects compiled
    /// with the worker on (both arrangements: the whole pipeline on one worker through
    /// <c>CompileAsync</c>, a handoff per native call through <c>Compile</c>) and off are
    /// identical on every target.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_WorkerOnAndOff_EmitsIdenticalBytes()
    {
        // A VS+PS effect with a matrix, a two-texture effect and a sin/cos effect (the GL
        // range-reduction rewrite); each compiles on all five targets.
        string[] fixtures = ["VertexAndPixel.fx", "MultiTexture.fx", "Dots.fx"];
        PlatformTarget[] targets = [PlatformTarget.OpenGL, PlatformTarget.DirectX, PlatformTarget.DirectX12, PlatformTarget.Vulkan, PlatformTarget.Fna];
        var compiler = new EffectCompiler();

        NativeCompileStack.Enabled.ShouldBeTrue("the worker is the shipped default");
        try
        {
            foreach (string fixture in fixtures)
            {
                string source = File.ReadAllText(TestHelpers.FixturePath(fixture));
                foreach (PlatformTarget target in targets)
                {
                    var options = new CompilerOptions
                    {
                        Target = target,
                        SourceFileName = fixture,
                        AdditionalIncludePaths = [Path.GetDirectoryName(TestHelpers.FixturePath(fixture))!],
                    };

                    NativeCompileStack.Enabled = true;
                    var onWorker = compiler.Compile(source, options);
                    var wholePipelineOnWorker = await compiler.CompileAsync(source, options);
                    NativeCompileStack.Enabled = false;
                    var inline = compiler.Compile(source, options);

                    onWorker.IsSuccess.ShouldBeTrue($"{fixture} {target}: " + (onWorker.IsFailure ? onWorker.Error[0].FxcFormattedMessage : ""));
                    wholePipelineOnWorker.IsSuccess.ShouldBeTrue($"{fixture} {target}: " + (wholePipelineOnWorker.IsFailure ? wholePipelineOnWorker.Error[0].FxcFormattedMessage : ""));
                    inline.IsSuccess.ShouldBeTrue($"{fixture} {target}: " + (inline.IsFailure ? inline.Error[0].FxcFormattedMessage : ""));
                    inline.Value.Data.ShouldBe(onWorker.Value.Data, $"{fixture} {target}: the bytes depend on the thread the native compiler ran on");
                    inline.Value.Data.ShouldBe(wholePipelineOnWorker.Value.Data, $"{fixture} {target}: the bytes depend on the thread the pipeline ran on");
                }
            }
        }
        finally
        {
            NativeCompileStack.Enabled = true;
        }
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(
        string shape, int depth, PlatformTarget target, string mode, TimeSpan watchdog)
    {
        string label = $"deep-shader probe {shape} x{depth} {target} {mode}";
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(DeepShaderProbe.ProbeArgument);
        psi.ArgumentList.Add(shape);
        psi.ArgumentList.Add(depth.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(target.ToString());
        psi.ArgumentList.Add(mode);

        ChildProcessResult run;
        try
        {
            run = await ChildProcess.RunAsync(psi, watchdog, label, captureHangEvidence: true);
        }
        catch (ChildProcessTimeoutException ex)
        {
            throw new ShouldAssertException($"{label} did not finish within {watchdog.TotalSeconds:0} s.\n{ex.Message}");
        }

        // A crash's stderr can be long (the runtime prints the whole managed stack); keep the head.
        string err = run.Stderr;
        string output = run.Stdout + (err.Length > 4000 ? err[..4000] + "\n[...]" : err);
        _output.WriteLine($"{label}: exit {run.ExitCode} (0x{run.ExitCode:X8}) after {run.Elapsed.TotalSeconds:F1} s\n{output}");
        return (run.ExitCode, output);
    }
}
