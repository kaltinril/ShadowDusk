#nullable enable

using System.Diagnostics;
using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Regression guards for the non-Windows test-host crash (see
/// <c>ShadowDusk.HLSL.Dxc.DxcSignalIsolation</c>). DXC's Unix build installs LLVM's signal
/// handlers over the .NET runtime's on every DXIL compile and preprocess, and races while doing
/// it. Unfixed, the process died three ways: SIGSEGV/SIGBUS in
/// <c>llvm::TargetRegistry::lookupTarget</c>, a hang re-faulting through LLVM's handler, or
/// exit 158 (killed by SIGUSR1, the signal CoreCLR uses for GC suspension on macOS).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcConcurrencyStressTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Probe stdout goes to the test output, so a passing run's .trx keeps the measurements.</summary>
    public DxcConcurrencyStressTests(ITestOutputHelper output) => _output = output;

    private const string Hlsl = """
        cbuffer Params { float4x4 World; float4 Tint; };
        Texture2D Tex; SamplerState Samp;
        struct VSOut { float4 Pos : SV_Position; float2 Uv : TEXCOORD0; };
        VSOut VSMain(float4 pos : POSITION, float2 uv : TEXCOORD0)
        { VSOut o; o.Pos = mul(pos, World); o.Uv = uv; return o; }
        float4 PSMain(VSOut i) : SV_Target
        {
            float4 c = Tex.Sample(Samp, i.Uv) * Tint;
            [loop] for (int k = 0; k < 4; k++) c.rgb += sin(c.gbr * k);
            return c;
        }
        """;

    /// <summary>
    /// The registration race is one-shot per process (it needs LLVM's signal table still
    /// empty), so each attempt runs <see cref="DxcConcurrencyProbe"/> in a fresh child process.
    /// Unfixed, about half the probes crashed or hung on macOS arm64, so ten attempts miss a
    /// regression with probability around 0.2%; each probe also checks deterministically that
    /// no DXC signal handler was left installed (exit 2). A crash or a hang both fail the test.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_ConcurrentFirstUseInFreshProcesses_NeverCrashesOrHangs()
    {
        const int attempts = 10;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            await RunProbeAsync(
                $"DXC first-use probe attempt {attempt}/{attempts}",
                TimeSpan.FromSeconds(60),
                DxcConcurrencyProbe.ProbeArgument);
        }
    }

    /// <summary>
    /// DXC compiles racing <c>Process.Start</c> and, on Linux, real libc <c>fork()</c> calls,
    /// in a fresh child process. Unfixed on macOS this deadlocked inside libc (see
    /// <c>DxcForkGate</c>); the probe would never exit, so a hang past the watchdog fails the
    /// test, with the hung child's native stacks and a core captured first
    /// (<see cref="HangDiagnostics"/>). On Linux it is the measurement behind leaving the gate
    /// off there (issue #256): glibc's fork path, exercised for real, against DXC's
    /// <c>setlocale</c> on four threads. Half of the compiles are SPIR-V with debug
    /// information, the issue #312 shape: DXC's SPIR-V emitter <c>dlopen</c>s its own library
    /// to read the source for <c>OpSource</c>, and a <c>fork()</c> that waited for such a
    /// compile under the first fork gate deadlocked with it on dyld's dlopen lock (measured in
    /// CI on macOS 26 arm64 with lldb stacks: issue #312).
    /// </summary>
    [UnixSignalFact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_ConcurrentWithProcessStartAndFork_NeverDeadlocks()
    {
        const int attempts = ForkProbeAttempts;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            string output = await RunProbeAsync(
                $"DXC-versus-fork probe attempt {attempt}/{attempts}",
                TimeSpan.FromSeconds(ForkProbeSeconds + 30),
                DxcConcurrencyProbe.ForkProbeArgument,
                ForkProbeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // A probe that raced nothing proves nothing: every operation must actually have run.
            Dictionary<string, int> counts = ParseCounts(output, "FORKPROBE");
            counts["compiles"].ShouldBeGreaterThan(0, output);
            counts["debugCompiles"].ShouldBeGreaterThan(0, output);
            counts["processStarts"].ShouldBeGreaterThan(0, output);
            if (OperatingSystem.IsLinux())
                counts["rawForks"].ShouldBeGreaterThan(0, output);
        }
    }

    private const int ForkProbeAttempts = 4;
    private const int ForkProbeSeconds = 6;

    /// <summary>
    /// Two halves of the premise behind <c>DxcForkGate</c> (issues #256 and #312). First: the
    /// only DXC calls that change the process locale are the native compile calls (the fresh
    /// process resets the C locale before each entry point ShadowDusk uses and reports which
    /// ones changed it; the raw native compile is the positive control, so a blind detector
    /// fails rather than passes). Second: once the locale has settled, no DXC call, the debug
    /// SPIR-V compile that <c>dlopen</c>s included, changes it again, so every later
    /// <c>setlocale</c> is a same-name call that cannot deadlock against <c>fork()</c> and the
    /// compiles can run ungated.
    /// </summary>
    [UnixSignalFact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task SetlocaleCalls_HappenOnlyInTheNativeCompileCalls_AndNeverOnceTheLocaleHasSettled()
    {
        string output = await RunProbeAsync(
            "DXC setlocale audit", TestBudget.Compile, DxcConcurrencyProbe.SetlocaleProbeArgument);

        Dictionary<string, bool> steps = output.Split('\n')
            .Where(l => l.StartsWith("STEP ", StringComparison.Ordinal))
            .Select(l => l.Trim().Split(' '))
            .ToDictionary(p => p[1], p => p[2] == "yes", StringComparer.Ordinal);

        steps.ContainsKey("compiler-dispose").ShouldBeTrue("the audit did not run to the end:\n" + output);
        steps[DxcSetlocaleAudit.PositiveControl].ShouldBeTrue(
            "the raw compile did not leave DXC's UTF-8 locale behind, so this detector cannot see " +
            "setlocale on this host (DXC fixed its restore bug, or the en_US UTF-8 locale is not " +
            "installed). Rework the detector before trusting the rest:\n" + output);

        string[] unexpected = steps
            .Where(s => s.Value && !DxcSetlocaleAudit.LocaleChangingSteps.Contains(s.Key))
            .Select(s => s.Key)
            .ToArray();
        unexpected.ShouldBeEmpty(
            "these DXC calls change the process locale although they are not native compile calls; " +
            "a new setlocale path in DXC must be measured against fork() before it is trusted:\n" + output);

        string settled = output.Split('\n')
            .Single(l => l.StartsWith("SETTLED ", StringComparison.Ordinal)).Trim()["SETTLED ".Length..];
        settled.ShouldNotBe("C", "the settle step did not change the locale, so the steady-state half measures nothing:\n" + output);

        string[] changedAfterSettling = output.Split('\n')
            .Where(l => l.StartsWith("STEADY ", StringComparison.Ordinal))
            .Select(l => l.Trim().Split(' '))
            .Where(p => p[2] != settled)
            .Select(p => $"{p[1]} -> {p[2]}")
            .ToArray();
        output.Split('\n').Count(l => l.StartsWith("STEADY ", StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(8, output);
        changedAfterSettling.ShouldBeEmpty(
            "a DXC call changed the process locale after it had settled, so its setlocale allocates under " +
            "the locale lock and the ungated compile can deadlock against a concurrent fork() on macOS " +
            "(issue #312 fix premise):\n" + output);
    }

    /// <summary>
    /// Pins the premise of where <c>DxcForkGate</c> is enabled (issue #256): an atfork gate only
    /// sees a <c>Process.Start</c> that really calls <c>fork()</c>. Measured: macOS .NET
    /// forks (the gate is needed and works); glibc Linux .NET uses <c>vfork()</c>, which runs
    /// no atfork handler and takes none of glibc's fork-time locks, so the macOS deadlock
    /// cannot form through <c>Process.Start</c> there. If the runtime ever changes this, the
    /// decision to keep the gate macOS-only must be revisited, and this test says so.
    /// </summary>
    [UnixSignalFact]
    public async Task ProcessStart_ForkMechanism_MatchesTheForkGatePlatforms()
    {
        string output = await RunProbeAsync(
            "Process.Start fork-mechanism probe", TestBudget.Compile, DxcConcurrencyProbe.ForkKindProbeArgument);

        string[] line = output.Split('\n').Single(l => l.StartsWith("ATFORK ", StringComparison.Ordinal)).Trim().Split(' ');
        int calls = int.Parse(line[1], System.Globalization.CultureInfo.InvariantCulture);
        int starts = int.Parse(line[2], System.Globalization.CultureInfo.InvariantCulture);

        if (OperatingSystem.IsMacOS())
        {
            calls.ShouldBeGreaterThanOrEqualTo(starts,
                "Process.Start no longer runs atfork handlers on macOS (posix_spawn?): DxcForkGate " +
                "no longer sees it. Re-measure the setlocale/fork deadlock and revisit the gate.");
        }
        else
        {
            calls.ShouldBe(0,
                "Process.Start now runs atfork handlers on Linux, so it is a real fork(): the " +
                "glibc analysis behind keeping DxcForkGate macOS-only (issue #256) no longer " +
                "covers it. Re-run the fork probe evidence and revisit the gate.");
        }
    }

    private static Dictionary<string, int> ParseCounts(string output, string prefix)
    {
        string line = output.Split('\n').Single(l => l.StartsWith(prefix + " ", StringComparison.Ordinal));
        return line.Trim().Split(' ').Skip(1)
            .Select(kv => kv.Split('='))
            .ToDictionary(kv => kv[0], kv => int.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }

    private async Task<string> RunProbeAsync(string label, TimeSpan watchdog, params string[] probeArguments)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        foreach (string argument in probeArguments)
            psi.ArgumentList.Add(argument);

        ChildProcessResult run;
        try
        {
            // Native stacks and a core are captured BEFORE the kill: the hang is the evidence.
            run = await ChildProcess.RunAsync(psi, watchdog, label, captureHangEvidence: true);
        }
        catch (ChildProcessTimeoutException ex)
        {
            throw new ShouldAssertException(
                $"{label} hung past its {watchdog.TotalSeconds:0} s watchdog (a native deadlock, or " +
                $"threads re-faulting forever in LLVM's SignalHandler).\n{ex.Message}");
        }

        string output = run.Output;
        _output.WriteLine($"{label}:\n{output}");
        run.ExitCode.ShouldBe(0,
            $"{label} exited {run.ExitCode} (a signal exit such as 134/138/139/158 is a native " +
            $"crash; 2 is a DXC signal handler left installed). Output:\n{output}");
        return output;
    }

    /// <summary>
    /// Deterministic in-process half: after DXIL compiles and a preprocess (the two call shapes
    /// that reach LLVM's <c>RegisterHandlers()</c>), no signal the runtime relies on may be
    /// handled by code inside the DXC image.
    /// </summary>
    [UnixSignalFact]
    [Trait("Platform", "DirectX")]
    public void Compile_DxilAndPreprocess_LeaveNoDxcSignalHandlerInstalled()
    {
        using var compiler = new DxcShaderCompiler();
        foreach (string entry in new[] { "PSMain", "VSMain" })
        {
            bool pixel = entry == "PSMain";
            var result = compiler.Compile(new DxcCompileRequest
            {
                HlslSource = Hlsl,
                SourceFileName = "signals.fx",
                EntryPoint = entry,
                Stage = pixel ? ShaderStage.Pixel : ShaderStage.Vertex,
                Platform = PlatformTarget.DirectX,
            });
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
        }

        var pp = compiler.Preprocess(new DxcPreprocessRequest { HlslSource = Hlsl, SourceFileName = "signals.fx" });
        pp.IsSuccess.ShouldBeTrue(pp.IsFailure ? pp.Error.FxcFormattedMessage : "");

        SignalHandlerOwnership.SignalsHandledBy("dxcompiler").ShouldBeEmpty(
            "DXC (LLVM Signals.inc) left its handlers installed over the .NET runtime's");
    }

    /// <summary>
    /// Steady-state concurrency in the test host itself, across all three DXC call shapes.
    /// </summary>
    [Fact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_ManyConcurrentInstancesDxilSpirvAndPreprocess_AllSucceed()
    {
        const int workers = 16;
        const int callsPerWorker = 8;

        Task<int>[] tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            int ok = 0;
            for (int i = 0; i < callsPerWorker; i++)
            {
                using var compiler = new DxcShaderCompiler();
                int kind = (w + i) % 3;

                if (kind == 2)
                {
                    var pp = compiler.Preprocess(new DxcPreprocessRequest
                    {
                        HlslSource = Hlsl,
                        SourceFileName = "stress.fx",
                    });
                    pp.IsSuccess.ShouldBeTrue(pp.IsFailure ? pp.Error.FxcFormattedMessage : "");
                    ok++;
                    continue;
                }

                bool pixel = ((w + i) & 1) == 0;
                var result = compiler.Compile(new DxcCompileRequest
                {
                    HlslSource = Hlsl,
                    SourceFileName = "stress.fx",
                    EntryPoint = pixel ? "PSMain" : "VSMain",
                    Stage = pixel ? ShaderStage.Pixel : ShaderStage.Vertex,
                    Platform = kind == 1 ? PlatformTarget.OpenGL : PlatformTarget.DirectX,
                });
                result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.FxcFormattedMessage : "");
                ok++;
            }
            return ok;
        })).ToArray();

        int[] counts = await Task.WhenAll(tasks);
        counts.Sum().ShouldBe(workers * callsPerWorker);
    }
}

/// <summary>
/// A fact that runs only where <see cref="SignalHandlerOwnership"/> can inspect signal handlers
/// (macOS, glibc Linux) and is reported as SKIPPED, not passed, everywhere else (Windows DXC
/// does not use LLVM's Unix signal code at all).
/// </summary>
public sealed class UnixSignalFactAttribute : FactAttribute
{
    public UnixSignalFactAttribute()
    {
        if (!SignalHandlerOwnership.IsSupported)
            Skip = "LLVM's Unix signal handlers only exist in DXC's macOS/Linux builds.";
    }
}
