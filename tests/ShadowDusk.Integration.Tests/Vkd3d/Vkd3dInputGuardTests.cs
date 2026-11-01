#nullable enable

using System.Diagnostics;
using ShadowDusk.Integration.Tests.Dxc;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ShadowDusk.Integration.Tests.Vkd3d;

/// <summary>
/// Source shapes the pinned vkd3d-shader cannot survive are refused before it sees them
/// (<c>Vkd3dInputGuard</c>). A cast to <c>void</c> crashed the process with an access violation
/// inside <c>vkd3d_shader_compile</c> on the DirectX 11 and FNA targets (issue #448). Each target
/// runs in a child process (<see cref="Vkd3dInputGuardProbe"/>), because unguarded the first case
/// took the process down and would have taken the test host with it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Vkd3dInputGuardTests
{
    private readonly ITestOutputHelper _output;

    public Vkd3dInputGuardTests(ITestOutputHelper output) => _output = output;

    /// <summary>What every vkd3d target must answer: the verdict prefix, and the line for a refusal.</summary>
    private static readonly Dictionary<string, (string Verdict, int Line)> Expected = new(StringComparer.Ordinal)
    {
        ["void-tex2d"] = ("DIAG SD0239", 3),
        ["void-local"] = ("DIAG SD0239", 3),
        ["void-zero"] = ("DIAG SD0239", 2),
        ["void-input"] = ("DIAG SD0239", 2),
        ["void-call"] = ("DIAG SD0239", 2),
        ["void-paren"] = ("DIAG SD0239", 2),
        ["void-spaced"] = ("DIAG SD0239", 3),
        ["void-in-expression"] = ("DIAG SD0239", 2),
        ["void-return"] = ("DIAG SD0239", 1),
        ["void-from-macro"] = ("DIAG SD0239", 3),
        ["void-type-from-macro"] = ("DIAG SD0239", 3),
        ["param-list-void"] = ("OK", 0),
        ["void-return-type"] = ("OK", 0),
        ["void-in-dead-branch"] = ("OK", 0),
        ["void-in-comments"] = ("OK", 0),
        ["void-in-unused-macro"] = ("OK", 0),
    };

    [Theory]
    [InlineData("DirectX")]
    [InlineData("Fna")]
    [Trait("Platform", "DirectX")]
    public async Task VoidCasts_AreRefusedWithSD0239_NeverCrashVkd3d(string target)
    {
        (int exitCode, string output) = await RunProbeAsync(target, TimeSpan.FromMinutes(4));

        exitCode.ShouldBe(0, $"the {target} probe died (an access-violation exit is vkd3d's crash back):\n{output}");
        output.ShouldContain(Vkd3dInputGuardProbe.VerdictPrefix + "DONE", Case.Sensitive);
        foreach (Vkd3dInputGuardProbe.Case c in Vkd3dInputGuardProbe.Cases)
        {
            (string verdict, int line) = Expected[c.Name];
            string actual = Verdict(output, c.Name);
            actual.ShouldStartWith(verdict, Case.Sensitive, $"{target} / {c.Name}");
            if (line > 0)
                actual.ShouldStartWith($"{verdict} {line}:", Case.Sensitive, $"{target} / {c.Name}: the refusal points at the cast");
        }
    }

    /// <summary>
    /// The reference: fxc's own engine (d3dcompiler_47, the DirectX oracle backend, which the guard
    /// does not cover) rejects every cast case itself and compiles every control, so SD0239 refuses
    /// only what <c>mgfxc</c> refuses too.
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "DirectX")]
    public async Task D3DCompilerOracle_RejectsEveryCastCase_AndCompilesEveryControl()
    {
        (int exitCode, string output) = await RunProbeAsync("d3dcompiler", TimeSpan.FromMinutes(4));

        exitCode.ShouldBe(0, output);
        output.ShouldContain(Vkd3dInputGuardProbe.VerdictPrefix + "DONE", Case.Sensitive);
        foreach (Vkd3dInputGuardProbe.Case c in Vkd3dInputGuardProbe.Cases)
        {
            string actual = Verdict(output, c.Name);
            if (Expected[c.Name].Verdict == "OK")
                actual.ShouldBe("OK", $"d3dcompiler / {c.Name}");
            else
                actual.ShouldStartWith("DIAG X3017", Case.Sensitive, $"d3dcompiler / {c.Name}");
        }
    }

    /// <summary>
    /// The positive control: the same pinned vkd3d-shader, handed a cast to <c>void</c> with no
    /// guard, must still crash, so the theory above proves the guard rather than a vkd3d that
    /// stopped faulting. When a vkd3d bump fixes the fault upstream this turns red, and the guard
    /// (and SD0239) can be reconsidered. Windows only: the crash was measured on win-x64.
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "DirectX")]
    public async Task RawVkd3d_StillCrashesOnAVoidCast_ProvingTheProbeCanDetectIt()
    {
        (int exitCode, string output) = await RunProbeAsync("raw-void-cast", TimeSpan.FromMinutes(2));

        output.ShouldContain(Vkd3dInputGuardProbe.VerdictPrefix + "RAW START", Case.Sensitive);
        output.ShouldNotContain(Vkd3dInputGuardProbe.VerdictPrefix + "RAW NOVKD3D", Case.Sensitive);
        output.ShouldNotContain(Vkd3dInputGuardProbe.VerdictPrefix + "RAW RETURNED", Case.Sensitive,
            "the pinned vkd3d-shader compiled a cast to void without crashing: the fault is gone, so SD0239 is " +
            "no longer needed\n" + output);
        exitCode.ShouldNotBe(0, output);
    }

    private static string Verdict(string output, string name)
    {
        string prefix = Vkd3dInputGuardProbe.VerdictPrefix + name + " ";
        string? line = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        line.ShouldNotBeNull($"no verdict for {name} (the probe died on or before it):\n{output}");
        return line[prefix.Length..];
    }

    private async Task<(int ExitCode, string Output)> RunProbeAsync(string target, TimeSpan watchdog)
    {
        string label = $"vkd3d input-guard probe {target}";
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(Vkd3dInputGuardProbe.ProbeArgument);
        psi.ArgumentList.Add(target);

        ChildProcessResult run;
        try
        {
            run = await ChildProcess.RunAsync(psi, watchdog, label, captureHangEvidence: true);
        }
        catch (ChildProcessTimeoutException ex)
        {
            throw new ShouldAssertException($"{label} did not finish within {watchdog.TotalSeconds:0} s.\n{ex.Message}");
        }

        // A crash's stderr carries the whole managed stack; keep the head.
        string err = run.Stderr;
        string output = run.Stdout + (err.Length > 4000 ? err[..4000] + "\n[...]" : err);
        _output.WriteLine($"{label}: exit {run.ExitCode} (0x{run.ExitCode:X8}) after {run.Elapsed.TotalSeconds:F1} s\n{output}");
        return (run.ExitCode, output);
    }
}
