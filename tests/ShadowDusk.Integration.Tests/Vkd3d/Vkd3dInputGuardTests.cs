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
/// took the process down and would have taken the test host with it. A macro that expands to itself
/// made vkd3d's preprocessor run forever (issue #449), which a hard deadline catches.
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

    /// <summary>What every vkd3d target must answer for the issue #449 cases.</summary>
    private static readonly Dictionary<string, string> ExpectedMacro = new(StringComparer.Ordinal)
    {
        ["fn-self-entry"] = "DIAG SD0240 2:",
        ["obj-self-entry"] = "DIAG SD0240 2:",
        ["fn-self"] = "DIAG SD0240 2:",
        ["obj-self-identifier"] = "DIAG SD0240 2:",
        ["obj-self-call"] = "DIAG SD0240 2:",
        ["fn-mutual"] = "DIAG SD0240 3:",
        ["obj-mutual"] = "DIAG SD0240 3:",
        ["obj-to-fn"] = "DIAG SD0240 3:",
        ["obj-self-in-if"] = "DIAG SD0240 2:",
        ["fn-self-unused"] = "OK",
        ["fn-self-name-without-call"] = "OK",
        ["fn-name-from-argument"] = "OK",
        ["obj-self-ifdef"] = "OK",
        ["obj-self-dead"] = "OK",
        ["nested-call"] = "OK",
        ["alias"] = "OK",
    };

    /// <summary>
    /// Issue #449: a macro that expands to itself (C stops at the repeated name; fxc and DXC compile
    /// it) made vkd3d's preprocessor run forever inside one native call, on FNA and on DirectX 11
    /// alike, so the compile never returned. It is refused with SD0240 before vkd3d sees it. The
    /// probe runs under a hard deadline: a hang is a failure here, never a stuck test run.
    /// </summary>
    [Theory]
    [InlineData("DirectX")]
    [InlineData("Fna")]
    [Trait("Platform", "DirectX")]
    public async Task SelfReferentialMacros_AreRefusedWithSD0240_NeverHangVkd3d(string target)
    {
        (int exitCode, string output) = await RunProbeAsync(target, TimeSpan.FromMinutes(2), "macro");

        exitCode.ShouldBe(0, output);
        output.ShouldContain(Vkd3dInputGuardProbe.VerdictPrefix + "DONE", Case.Sensitive);
        foreach (Vkd3dInputGuardProbe.Case c in Vkd3dInputGuardProbe.MacroCases)
            Verdict(output, c.Name).ShouldStartWith(ExpectedMacro[c.Name], Case.Sensitive, $"{target} / {c.Name}");
    }

    /// <summary>
    /// The reference for issue #449: fxc's own engine (the d3dcompiler_47 oracle) compiles every
    /// refused case, so SD0240 is a refusal of what <c>mgfxc</c> builds, made only because vkd3d
    /// hangs. One control differs the other way: fxc's preprocessor reads the self-named macro used
    /// without a call as a call to the variable (<c>X3005</c>), which vkd3d and DXC compile.
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "DirectX")]
    public async Task D3DCompilerOracle_CompilesEverySelfReferentialMacroCase()
    {
        (int exitCode, string output) = await RunProbeAsync("d3dcompiler", TimeSpan.FromMinutes(2), "macro");

        exitCode.ShouldBe(0, output);
        foreach (Vkd3dInputGuardProbe.Case c in Vkd3dInputGuardProbe.MacroCases)
        {
            Verdict(output, c.Name).ShouldStartWith(
                c.Name == "fn-self-name-without-call" ? "DIAG X3005" : "OK", Case.Sensitive, $"d3dcompiler / {c.Name}");
        }
    }

    /// <summary>
    /// The positive control of issue #449: the same pinned vkd3d-shader, handed <c>#define K K</c> with
    /// no guard, must still not return within the deadline (the child is killed), so the theory
    /// above proves the guard rather than a vkd3d that stopped hanging. When a vkd3d bump fixes the
    /// preprocessor this turns red, and SD0240 can be retired. Windows only, as measured (win-x64).
    /// </summary>
    [WindowsOnlyFact]
    [Trait("Platform", "DirectX")]
    public async Task RawVkd3d_StillHangsOnASelfReferentialMacro_ProvingTheProbeCanDetectIt()
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(Vkd3dInputGuardProbe.ProbeArgument);
        psi.ArgumentList.Add("raw-self-macro");

        // Measured: every guarded case above returns in well under a second, and the raw call runs
        // until it is killed. 20 s is far past any real compile of this three-line shader.
        ChildProcessTimeoutException hung = await Should.ThrowAsync<ChildProcessTimeoutException>(
            () => ChildProcess.RunAsync(psi, TimeSpan.FromSeconds(20), "vkd3d raw self-referential macro"),
            "the pinned vkd3d-shader returned on a self-referential macro: its preprocessor no longer hangs, so " +
            "SD0240 is no longer needed");
        _output.WriteLine(hung.Message);
        hung.Stdout.ShouldContain(Vkd3dInputGuardProbe.VerdictPrefix + "RAW START", Case.Sensitive);
        hung.Stdout.ShouldNotContain(Vkd3dInputGuardProbe.VerdictPrefix + "RAW RETURNED", Case.Sensitive);
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

    private async Task<(int ExitCode, string Output)> RunProbeAsync(string target, TimeSpan watchdog, string? group = null)
    {
        string label = $"vkd3d input-guard probe {target} {group}".TrimEnd();
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        var psi = new ProcessStartInfo(dotnet);
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(DxcConcurrencyProbe).Assembly.Location);
        psi.ArgumentList.Add(Vkd3dInputGuardProbe.ProbeArgument);
        psi.ArgumentList.Add(target);
        if (group is not null)
            psi.ArgumentList.Add(group);

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
