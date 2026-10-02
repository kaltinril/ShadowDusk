#nullable enable

using System.Diagnostics;
using System.Globalization;
using ShadowDusk.Integration.Tests.Dxc;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// Issue #316: the shared <see cref="ChildProcess"/> helper every child-process-spawning test
/// now goes through. Each test re-runs this assembly as a fresh child (the
/// <see cref="DxcConcurrencyProbe"/> entry point dispatches <see cref="ProbeArgument"/> here)
/// in a mode that reproduces one way the old per-class helpers went wrong.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChildProcessTests
{
    /// <summary>The argument that selects these probes in the re-run test assembly.</summary>
    public const string ProbeArgument = "--child-process-probe";

    /// <summary>Far more than any OS pipe buffer (4 KiB to 64 KiB), so an undrained pipe blocks the writer.</summary>
    private const int FloodChars = 1024 * 1024;

    private const string StdoutMarker = "stdout-written-before-the-hang";
    private const string StderrMarker = "stderr-written-before-the-hang";

    /// <summary>How long a probe may take to come up; it only bounds a failure, never a pass.</summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The latent deadlock in the old helpers: they read stdout to the end and only then
    /// stderr. A child that writes more than a pipe buffer to stderr first blocks in that
    /// write, never closes stdout, and the test waits forever on a compile that had failed
    /// with a long message.
    /// </summary>
    [Fact]
    public async Task ChildThatFillsStderrBeforeStdout_IsDrainedWithoutDeadlock()
    {
        ChildProcessResult run = await ChildProcess.RunAsync(Probe("flood"), StartupBudget, "flood probe");

        run.ExitCode.ShouldBe(7);
        run.Stderr.Length.ShouldBe(FloodChars);
        run.Stdout.Length.ShouldBe(FloodChars);
        run.Stderr.All(c => c == 'e').ShouldBeTrue("stderr must arrive intact");
        run.Stdout.All(c => c == 'o').ShouldBeTrue("stdout must arrive intact");
    }

    /// <summary>The blocking form drains both pipes too, and feeds stdin without deadlocking against them.</summary>
    [Fact]
    public void BlockingRun_FeedsStdinAndDrainsBothPipes()
    {
        string input = new('i', FloodChars);

        ChildProcessResult run = ChildProcess.Run(Probe("echo"), StartupBudget, "echo probe", standardInput: input);

        run.ExitCode.ShouldBe(0);
        run.Stdout.Length.ShouldBe(FloodChars);
        (run.Stdout == input).ShouldBeTrue("stdin must come back on stdout intact");
        run.Stderr.Length.ShouldBe(FloodChars);
    }

    /// <summary>
    /// A child that never exits is killed when its time is up, and the failure says so with the
    /// elapsed time and the command line instead of a bare cancellation.
    /// </summary>
    [Fact]
    public async Task HungChild_TimesOut_IsKilled_AndTheFailureNamesTheCommand()
    {
        var timeout = TimeSpan.FromSeconds(2);

        var ex = await Should.ThrowAsync<ChildProcessTimeoutException>(
            () => ChildProcess.RunAsync(Probe("hang"), timeout, "hang probe"));

        ex.Elapsed.ShouldBeGreaterThanOrEqualTo(timeout);
        ex.Message.ShouldContain("hang probe did not finish within its 2 s timeout", Case.Sensitive);
        ex.Message.ShouldContain("command: ", Case.Sensitive);
        ex.Message.ShouldContain(ProbeArgument + " hang", Case.Sensitive);
        ex.Message.ShouldContain("the process is gone", Case.Sensitive);
        (await IsGoneAsync(ex.ProcessId)).ShouldBeTrue("the timed-out child must not be left running");
    }

    /// <summary>
    /// The evidence the old helpers threw away: what the child had written to BOTH pipes before
    /// it stalled. Also the whole tree dies, not just the direct child: a surviving grandchild
    /// would keep the pipes open and the machine loaded.
    /// </summary>
    [Fact]
    public async Task HungChildWithAGrandchild_ReportsTheOutputSoFar_AndTheWholeTreeIsKilled()
    {
        string ready = ReadyFile();
        try
        {
            using var deadline = new CancellationTokenSource();
            Task<ChildProcessResult> run = ChildProcess.RunAsync(
                Probe("tree", ready), TimeSpan.FromMinutes(10), "tree probe", cancellationToken: deadline.Token);

            // The grandchild writes the ready file, so by now the parent has flushed its markers.
            int grandchildPid = int.Parse(await ReadWhenReadyAsync(ready, run), CultureInfo.InvariantCulture);
            deadline.Cancel();

            var ex = await Should.ThrowAsync<ChildProcessTimeoutException>(() => run);

            ex.Stdout.ShouldContain(StdoutMarker, Case.Sensitive);
            ex.Stderr.ShouldContain(StderrMarker, Case.Sensitive);
            ex.Message.ShouldContain(StdoutMarker, Case.Sensitive);
            ex.Message.ShouldContain(StderrMarker, Case.Sensitive);
            ex.Message.ShouldContain("was still running when the caller's deadline was cancelled", Case.Sensitive);
            ex.Message.ShouldContain($"process {ex.ProcessId} still running: CPU ", Case.Sensitive);
            ex.Message.ShouldContain("both pipes reached EOF", Case.Sensitive);

            (await IsGoneAsync(ex.ProcessId)).ShouldBeTrue("the child must be killed");
            (await IsGoneAsync(grandchildPid)).ShouldBeTrue("the grandchild must be killed with it");
        }
        finally
        {
            File.Delete(ready);
        }
    }

    /// <summary>
    /// The pre-kill dump of a hung .NET child, which is what a timed-out CLI compile gets. Only
    /// Windows asserts the dump itself (it is the OS issue #316 is about, and needs no ptrace
    /// right); everywhere the report must exist and say what each tool did. This test is why
    /// Windows uses <c>MiniDumpWriteDump</c>: it caught <c>createdump.exe</c> refusing a pid.
    /// </summary>
    [Fact]
    public async Task HungChild_EvidenceCapture_WritesAReportAndOnWindowsADump()
    {
        string ready = ReadyFile();
        string dumps = Path.Combine(Path.GetTempPath(), "sd-child-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var deadline = new CancellationTokenSource();
            Task<ChildProcessResult> run = ChildProcess.RunAsync(
                Probe("hang", ready), TimeSpan.FromMinutes(10), "hang probe", cancellationToken: deadline.Token);
            int pid = int.Parse(await ReadWhenReadyAsync(ready, run), CultureInfo.InvariantCulture);

            string evidence = await HangDiagnostics.CaptureAsync(pid, "hang probe", dumps);
            deadline.Cancel();
            await Should.ThrowAsync<ChildProcessTimeoutException>(() => run);

            string report = Path.Combine(dumps, $"child-{pid}-stacks.txt");
            File.Exists(report).ShouldBeTrue(evidence);
            (await File.ReadAllTextAsync(report)).ShouldContain(
                OperatingSystem.IsWindows() ? "MiniDumpWriteDump" : "createdump", Case.Sensitive);
            if (OperatingSystem.IsWindows())
            {
                var dump = new FileInfo(Path.Combine(dumps, $"child-{pid}.dmp"));
                dump.Exists.ShouldBeTrue(evidence);
                dump.Length.ShouldBeGreaterThan(0, evidence);
            }
        }
        finally
        {
            File.Delete(ready);
            if (Directory.Exists(dumps))
                Directory.Delete(dumps, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The probes (run in the child)
    // ---------------------------------------------------------------------------------------

    /// <summary>Entry point for the re-run test assembly; <paramref name="args"/> starts at the mode.</summary>
    internal static int RunProbe(string[] args)
    {
        switch (args[0])
        {
            case "flood":
                // stderr FIRST: with nobody draining it, this write blocks before stdout is touched.
                Console.Error.Write(new string('e', FloodChars));
                Console.Error.Flush();
                Console.Out.Write(new string('o', FloodChars));
                Console.Out.Flush();
                return 7;

            case "echo":
                Console.Error.Write(new string('e', FloodChars));
                Console.Error.Flush();
                Console.Out.Write(Console.In.ReadToEnd());
                Console.Out.Flush();
                return 0;

            case "hang":
                WriteMarkers();
                if (args.Length > 1)
                    File.WriteAllText(args[1], Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
                BlockForever();
                return 0;

            case "tree":
                WriteMarkers();
                // No redirection: the grandchild inherits this process's stdout and stderr, so
                // it alone would keep the test's pipes open after this process is killed.
                ProcessStartInfo grandchild = Probe("hang", args[1]);
                using (Process.Start(grandchild))
                {
                    BlockForever();
                }
                return 0;

            default:
                Console.Error.WriteLine($"unknown child-process probe mode '{args[0]}'");
                return 2;
        }
    }

    private static void WriteMarkers()
    {
        Console.Out.Write(StdoutMarker);
        Console.Out.Flush();
        Console.Error.Write(StderrMarker);
        Console.Error.Flush();
    }

    private static void BlockForever()
    {
        using var never = new ManualResetEventSlim(false);
        never.Wait();
    }

    // ---------------------------------------------------------------------------------------
    // Helpers (run in the test)
    // ---------------------------------------------------------------------------------------

    private static ProcessStartInfo Probe(params string[] arguments)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self) == "dotnet"
                ? self
                : "dotnet";

        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(ChildProcessTests).Assembly.Location);
        psi.ArgumentList.Add(ProbeArgument);
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    private static string ReadyFile() =>
        Path.Combine(Path.GetTempPath(), "sd-child-ready-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>
    /// Waits for the probe to write its ready file (a pid and a newline). Fails with the run's
    /// own error if the probe died first.
    /// </summary>
    private static async Task<string> ReadWhenReadyAsync(string path, Task<ChildProcessResult> run)
    {
        using var budget = new CancellationTokenSource(StartupBudget);
        while (true)
        {
            if (run.IsCompleted)
            {
                ChildProcessResult early = await run;
                throw new ShouldAssertException($"the probe exited ({early.ExitCode}) before it was ready:\n{early.Output}");
            }

            try
            {
                string text = await File.ReadAllTextAsync(path, budget.Token);
                if (text.EndsWith('\n'))
                    return text.Trim();
            }
            catch (IOException)
            {
                // Not created yet, or the probe is mid-write.
            }

            await Task.Delay(50, budget.Token);
        }
    }

    private static async Task<bool> IsGoneAsync(int pid)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!budget.IsCancellationRequested)
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true; // no such process
            }
            catch (InvalidOperationException)
            {
                return true; // exited between the lookup and the query
            }

            try
            {
                await Task.Delay(50, budget.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return false;
    }
}
