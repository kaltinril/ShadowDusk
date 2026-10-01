#nullable enable

using System.Diagnostics;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The test assembly's entry point (the test SDK's generated empty <c>Main</c> is switched off
/// with <c>GenerateProgramFile=false</c>). With <see cref="ProbeArgument"/> it runs the DXC
/// first-use concurrency race, and with <see cref="ForkProbeArgument"/> DXC compiles racing
/// <c>Process.Start</c>, each in a FRESH process for <see cref="DxcConcurrencyStressTests"/>;
/// with any other arguments it does nothing, exactly like the generated <c>Main</c>.
/// </summary>
/// <remarks>
/// A child process is required because both failures take the whole process with them (a
/// native crash, or a permanent libc deadlock), and the first-use race is one-shot per process:
/// LLVM's <c>RegisterHandlers()</c> only races while its signal table is still empty.
/// </remarks>
public static class DxcConcurrencyProbe
{
    /// <summary>The argument that selects the probe.</summary>
    public const string ProbeArgument = "--dxc-concurrency-probe";

    /// <summary>The argument that selects the DXC-versus-fork probe.</summary>
    public const string ForkProbeArgument = "--dxc-fork-probe";

    private const string Hlsl = """
        cbuffer Params { float4x4 World; float4 Tint; };
        float4 PSMain(float4 p : SV_Position) : SV_Target { return mul(p, World) * Tint; }
        """;

    /// <summary>Entry point; returns 0 when every concurrent DXC call succeeded.</summary>
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ForkProbeArgument)
            return RunForkProbe(TimeSpan.FromSeconds(double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture)));
        if (args.Length == 0 || args[0] != ProbeArgument)
            return 0;

        // Few workers on purpose: measured unfixed on macOS arm64, 4 workers crashed ~47% of
        // fresh processes against ~25% for 16 or 32 (more threads spread the barrier wake-up
        // and narrow the overlap inside RegisterHandlers()).
        const int workers = 4;
        const int callsPerWorker = 4;
        int failures = 0;

        using var barrier = new Barrier(workers);
        Thread[] threads = Enumerable.Range(0, workers).Select(w => new Thread(() =>
        {
            using var compiler = new DxcShaderCompiler();

            // Every worker's FIRST call is a DXIL compile, released together, so the
            // first-use signal-handler registration is entered by many threads at once.
            barrier.SignalAndWait();

            for (int i = 0; i < callsPerWorker; i++)
            {
                string? error = (i % 3) switch
                {
                    1 => Describe(compiler.Preprocess(new DxcPreprocessRequest
                    {
                        HlslSource = Hlsl,
                        SourceFileName = "probe.fx",
                    })),
                    2 => Describe(Compile(compiler, PlatformTarget.OpenGL)),
                    _ => Describe(Compile(compiler, PlatformTarget.DirectX)),
                };

                if (error is not null)
                {
                    Console.Error.WriteLine(error);
                    Interlocked.Increment(ref failures);
                }
            }
        })).ToArray();

        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();
        if (failures != 0) return 1;

        // The deterministic half: DXC must not have left LLVM's handlers installed.
        IReadOnlyList<string> leaked = SignalHandlerOwnership.SignalsHandledBy("dxcompiler");
        foreach (string line in leaked)
            Console.Error.WriteLine("signal handler left installed by DXC: " + line);
        return leaked.Count == 0 ? 0 : 2;
    }

    /// <summary>
    /// DXC compiles on several threads while others start processes (every
    /// <c>Process.Start</c> on Unix is a <c>fork()</c>). Unfixed on macOS this deadlocks inside
    /// libc (DXC's <c>setlocale</c> against <c>fork()</c>'s atfork locking; see
    /// <c>DxcForkGate</c>), so the parent's watchdog, not this method, reports the failure.
    /// </summary>
    private static int RunForkProbe(TimeSpan duration)
    {
        DateTime stop = DateTime.UtcNow + duration;
        int failures = 0;

        IEnumerable<Thread> compilers = Enumerable.Range(0, 4).Select(w => new Thread(() =>
        {
            using var compiler = new DxcShaderCompiler();
            while (DateTime.UtcNow < stop)
            {
                string? error = Describe(Compile(compiler, (w & 1) == 0 ? PlatformTarget.DirectX : PlatformTarget.OpenGL));
                if (error is not null)
                {
                    Console.Error.WriteLine(error);
                    Interlocked.Increment(ref failures);
                    return;
                }
            }
        }));

        IEnumerable<Thread> forkers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                using Process process = Process.Start(new ProcessStartInfo("/usr/bin/true") { UseShellExecute = false })
                    ?? throw new InvalidOperationException("Process.Start returned null");
                process.WaitForExit();
            }
        }));

        Thread[] threads = [.. compilers, .. forkers];
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();
        return failures == 0 ? 0 : 1;
    }

    private static Result<PlatformBlob, ShaderError> Compile(DxcShaderCompiler compiler, PlatformTarget platform)
        => compiler.Compile(new DxcCompileRequest
        {
            HlslSource = Hlsl,
            SourceFileName = "probe.fx",
            EntryPoint = "PSMain",
            Stage = ShaderStage.Pixel,
            Platform = platform,
        });

    private static string? Describe<T>(Result<T, ShaderError> result)
        => result.IsFailure ? result.Error.FxcFormattedMessage : null;
}
