#nullable enable

using System.Diagnostics;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #469: <see cref="SlangCompiler"/> once waited on the slangc child with a bare
/// <c>WaitForExit()</c>, so a slangc that never exited hung a consumer's <c>CompileAsync</c>
/// forever. A run now has a timeout (<see cref="SlangCompiler.SlangcTimeout"/>) and honours
/// the compile's <see cref="CancellationToken"/>: on either, the child's process tree is
/// killed and the compile fails loudly (<c>SD0645</c>) or cancels.
/// </summary>
public sealed class SlangcTimeoutTests
{
    private const string FakeSlangcPath = "fake-tools/slangc";

    private const string Fragment =
        "[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return float4(uv, 0, 1); }\n";

    private static SlangCompiler Compiler(SlangCompiler.SlangcInvoker runSlangc) =>
        new(new ScriptedSlangc.CapturingCompiler(),
            () => new SlangCompiler.SlangcLocation(null, FakeSlangcPath),
            prepareSlangc: path => path,
            runSlangc: runSlangc);

    /// <summary>A child that never exits on its own, for the process-route tests: the shell
    /// (always present) sleeping far longer than any timeout under test.</summary>
    private static (string Path, string[] Arguments) NeverExitingChild() =>
        OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/c", "ping", "-n", "1000", "127.0.0.1"])
            : ("/bin/sh", ["-c", "sleep 1000"]);

    [Fact]
    [Trait("Category", "Integration")]
    public void RunSlangc_KillsAChildThatOutlivesTheTimeout_AndThrowsNamingTheRun()
    {
        (string path, string[] arguments) = NeverExitingChild();
        var clock = Stopwatch.StartNew();

        var ex = Should.Throw<TimeoutException>(() =>
            SlangCompiler.RunSlangc(path, Environment.CurrentDirectory, Fragment, arguments,
                TimeSpan.FromSeconds(2), CancellationToken.None));

        // A 1000 s child, back in well under that: the wait ended at the timeout and the kill
        // took (a WaitForExit after the kill is what reaps it, so a survivor would hang here).
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        ex.Message.ShouldContain("did not exit within 2 s", Case.Sensitive);
        ex.Message.ShouldContain(path, Case.Sensitive);
        ex.Message.ShouldContain(string.Join(' ', arguments), Case.Sensitive);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunSlangc_KillsTheChild_WhenTheTokenIsCancelled_BeforeTheTimeout()
    {
        (string path, string[] arguments) = NeverExitingChild();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        Should.Throw<OperationCanceledException>(() =>
            SlangCompiler.RunSlangc(path, Environment.CurrentDirectory, Fragment, arguments,
                TimeSpan.FromSeconds(60), cancellation.Token));

        // Cancelled at 0.5 s, not timed out at 60 s.
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void RunSlangc_RefusesANonPositiveTimeout_BeforeStartingAnything()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            SlangCompiler.RunSlangc("nonexistent-slangc", Environment.CurrentDirectory, Fragment, ["-E"],
                TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public void RunSlangc_ObservesAnAlreadyCancelledToken_BeforeStartingAnything()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Were the process started, a nonexistent path would throw Win32Exception instead.
        Should.Throw<OperationCanceledException>(() =>
            SlangCompiler.RunSlangc("nonexistent-slangc", Environment.CurrentDirectory, Fragment, ["-E"],
                TimeSpan.FromSeconds(1), cancellation.Token));
    }

    [Fact]
    public void Compile_ReportsATimedOutSlangc_AsSd0645_WithTheRunNamed()
    {
        const string reason = "slangc did not exit within 60 s and was killed: 'fake-tools/slangc' -entry MainPS -stage fragment -- -.";
        var compiler = Compiler((_, _, _, _, _) => throw new TimeoutException(reason));

        var result = compiler.Compile(Fragment, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Hang.slang" });

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe(SlangCompiler.SlangcTimedOutCode);
        error.File.ShouldBe("Hang.slang");
        error.Message.ShouldStartWith(reason, Case.Sensitive);
        error.Message.ShouldContain("SlangcTimeout", Case.Sensitive);
    }

    [Fact]
    public void Compile_PropagatesCancellation_FromTheSlangcRun()
    {
        var compiler = Compiler((_, _, _, _, token) => throw new OperationCanceledException(token));

        Should.Throw<OperationCanceledException>(() =>
            compiler.Compile(Fragment, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Cancel.slang" }));
    }

    [Fact]
    public void Compile_HandsTheCompilesToken_ToTheSlangcRun()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken seen = default;
        var compiler = Compiler((_, _, _, arguments, token) =>
        {
            seen = token;
            return arguments.Contains("-entry") ? (0, "float4 MainPS() : SV_Target { return 1; }\n", "") : (0, "", "");
        });

        var result = compiler.Compile(Fragment, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Token.slang" }, cancellation.Token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "");
        seen.ShouldBe(cancellation.Token);
    }

    [Fact]
    public void SlangcTimeout_DefaultsToAMinute_AndIsSettable()
    {
        new SlangCompiler(new ScriptedSlangc.CapturingCompiler()).SlangcTimeout.ShouldBe(SlangCompiler.DefaultSlangcTimeout);
        SlangCompiler.DefaultSlangcTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        new SlangCompiler(new ScriptedSlangc.CapturingCompiler()) { SlangcTimeout = TimeSpan.FromSeconds(5) }
            .SlangcTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        new SlangCompiler(new ScriptedSlangc.CapturingCompiler()) { SlangcTimeout = Timeout.InfiniteTimeSpan }
            .SlangcTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SlangcTimeout_RefusesANonPositiveValue_WhenSet(int seconds)
    {
        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            new SlangCompiler(new ScriptedSlangc.CapturingCompiler()) { SlangcTimeout = TimeSpan.FromSeconds(seconds) });
        ex.ParamName.ShouldBe("SlangcTimeout");
    }

    [Fact]
    public void RunSlangc_TreatsATimeoutBeyondWhatATimerHolds_AsNoLimit_NotAnOverflow()
    {
        // TimeSpan.MaxValue is a natural spelling of "no limit"; a child that exits at once
        // must come back normally (an overflow would throw before it even started).
        (string path, string[] arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "exit", "0" })
            : ("/bin/sh", new[] { "-c", "exit 0" });

        (int exitCode, _, _) = SlangCompiler.RunSlangc(path, Environment.CurrentDirectory, Fragment, arguments,
            TimeSpan.MaxValue, CancellationToken.None);

        exitCode.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunSlangc_TimesOut_EvenWhenTheChildNeverReadsASourceLargerThanThePipeBuffer()
    {
        // The stdin write blocks once the pipe buffer is full until the child reads; a child
        // stuck before reading must still be killed at the timeout (review of issue #469).
        (string path, string[] arguments) = NeverExitingChild();
        string bigSource = new string('/', 1 << 20) + "\n" + Fragment;
        var clock = Stopwatch.StartNew();

        var ex = Should.Throw<TimeoutException>(() =>
            SlangCompiler.RunSlangc(path, Environment.CurrentDirectory, bigSource, arguments,
                TimeSpan.FromSeconds(2), CancellationToken.None));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        ex.Message.ShouldContain("did not exit within 2 s", Case.Sensitive);
    }
}
