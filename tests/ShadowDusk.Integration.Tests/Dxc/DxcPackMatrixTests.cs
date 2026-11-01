#nullable enable

using System.Diagnostics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// Issue #452: a <c>#pragma pack_matrix</c> argument DXC rejects (<c>bogus</c>, <c>ROW_MAJOR</c>,
/// <c>()</c>, ...) is DXC's error, formatted from state DXC did not set, and the pinned DXC crashed
/// the process while reporting it: intermittently under parallel load on DirectX 12, and on every
/// fresh process that compiles it a few hundred times in a row (measured on win-x64). It is now
/// refused with SD0241 before any DXC call (<c>DxcPackMatrixGuard</c>). Every compile runs in a child
/// process (<see cref="DxcPackMatrixProbe"/>), on several threads at once, because unguarded the
/// fault took the whole process with it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcPackMatrixTests
{
    private readonly ITestOutputHelper _output;

    public DxcPackMatrixTests(ITestOutputHelper output) => _output = output;

    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["reported"] = "DIAG SD0241 1:21",   // at the argument, where DXC's own error points
        ["bogus"] = "DIAG SD0241 1:21",
        ["upper"] = "DIAG SD0241 1:21",
        ["empty"] = "DIAG SD0241 1:21",
        ["macro-bogus"] = "DIAG SD0241 2:21",
        ["operator"] = "DIAG SD0241 1:1",
        ["row"] = "OK",
        ["macro-row"] = "OK",
        ["dead-bogus"] = "OK",
    };

    [Theory]
    [InlineData("DirectX12", 8, 10)]
    [InlineData("OpenGL", 4, 4)]
    [InlineData("Vulkan", 4, 4)]
    [Trait("Platform", "DirectX12")]
    [Trait("Platform", "OpenGL")]
    [Trait("Platform", "Vulkan")]
    public async Task InvalidPackMatrixArguments_AreRefusedWithSD0241_UnderParallelLoad_NeverCrashDxc(
        string target, int threads, int rounds)
    {
        (int exitCode, string output) = await RunProbeAsync(target, threads, rounds);

        exitCode.ShouldBe(0, $"the {target} probe died (an access-violation exit is DXC's crash back):\n{output}");
        output.ShouldContain(DxcPackMatrixProbe.Prefix + "DONE", Case.Sensitive);
        foreach ((string name, _) in DxcPackMatrixProbe.Cases)
        {
            // One verdict per case, every time, on every thread.
            string[] lines = output.Split('\n').Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith($"{DxcPackMatrixProbe.Prefix}{name} ", StringComparison.Ordinal)).ToArray();
            lines.Length.ShouldBe(1, $"{target} / {name}:\n{output}");
            lines[0].ShouldBe($"{DxcPackMatrixProbe.Prefix}{name} {Expected[name]} x{threads * rounds}", $"{target} / {name}");
        }
    }

    /// <summary>
    /// The positive control: the same pinned DXC, called directly with the DirectX 12 arguments and
    /// no guard, compiling the invalid pragma 400 times on one thread, must still crash the process
    /// (measured: every fresh process did), so the theory above proves the guard rather than a DXC
    /// that stopped faulting. When a DXC bump fixes the pragma handler this turns red, and SD0241 can
    /// be reconsidered. Windows only: the crash was measured on win-x64.
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "DirectX12")]
    public async Task RawDxc_StillCrashesOnAnInvalidPackMatrixArgument_ProvingTheProbeCanDetectIt()
    {
        const int attempts = 3;
        var outputs = new List<string>();
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            (int exitCode, string output) = await RunProbeAsync("raw", threads: 1, rounds: 400);
            output.ShouldContain(DxcPackMatrixProbe.Prefix + "RAW START", Case.Sensitive);
            output.ShouldNotContain(DxcPackMatrixProbe.Prefix + "RAW NODXC", Case.Sensitive);
            if (exitCode != 0 && !output.Contains(DxcPackMatrixProbe.Prefix + "RAW RETURNED", StringComparison.Ordinal))
                return;
            outputs.Add(output);
        }

        throw new ShouldAssertException(
            $"the pinned DXC compiled an invalid '#pragma pack_matrix' 400 times in {attempts} fresh processes without " +
            "crashing: the fault is gone, so SD0241 may no longer be needed\n" + string.Join("\n---\n", outputs));
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(string target, int threads, int rounds)
    {
        string label = $"pack_matrix probe {target} {threads}x{rounds}";
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(DxcPackMatrixProbe.ProbeArgument);
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(rounds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        TimeSpan watchdog = TimeSpan.FromMinutes(4);
        ChildProcessResult run;
        try
        {
            run = await ChildProcess.RunAsync(psi, watchdog, label, captureHangEvidence: true);
        }
        catch (ChildProcessTimeoutException ex)
        {
            throw new ShouldAssertException($"{label} did not finish within {watchdog.TotalSeconds:0} s.\n{ex.Message}");
        }

        string err = run.Stderr;
        string output = run.Stdout + (err.Length > 4000 ? err[..4000] + "\n[...]" : err);
        _output.WriteLine($"{label}: exit {run.ExitCode} (0x{run.ExitCode:X8}) after {run.Elapsed.TotalSeconds:F1} s\n{output}");
        return (run.ExitCode, output);
    }
}
