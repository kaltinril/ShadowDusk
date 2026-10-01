#nullable enable

using System.Diagnostics;
using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Dxc;
using Xunit;

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
    /// DXC compiles racing <c>Process.Start</c> (a <c>fork()</c>) in a fresh child process.
    /// Unfixed on macOS this deadlocked inside libc (see <c>DxcForkGate</c>); the probe would
    /// never exit, so a hang past the watchdog fails the test.
    /// </summary>
    [UnixSignalFact]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "DirectX")]
    public async Task Compile_ConcurrentWithProcessStart_NeverDeadlocks()
    {
        const int attempts = ForkProbeAttempts;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            await RunProbeAsync(
                $"DXC-versus-fork probe attempt {attempt}/{attempts}",
                TimeSpan.FromSeconds(ForkProbeSeconds + 30),
                DxcConcurrencyProbe.ForkProbeArgument,
                ForkProbeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private const int ForkProbeAttempts = 4;
    private const int ForkProbeSeconds = 4;

    private static async Task RunProbeAsync(string label, TimeSpan watchdog, params string[] probeArguments)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        foreach (string argument in probeArguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"{label}: failed to start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(watchdog);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new ShouldAssertException(
                $"{label} hung past its {watchdog.TotalSeconds:0} s watchdog (a native deadlock, or " +
                "threads re-faulting forever in LLVM's SignalHandler).");
        }

        string output = await stdout + await stderr;
        process.ExitCode.ShouldBe(0,
            $"{label} exited {process.ExitCode} (a signal exit such as 134/138/139/158 is a native " +
            $"crash; 2 is a DXC signal handler left installed). Output:\n{output}");
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
