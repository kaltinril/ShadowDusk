#nullable enable

using System.Diagnostics;
using System.Text;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Runs alone: these tests saturate the process-wide thread pool, which would stall
/// every test running beside them.</summary>
[CollectionDefinition(nameof(SaturatedThreadPoolCollection), DisableParallelization = true)]
public sealed class SaturatedThreadPoolCollection
{
}

/// <summary>
/// A slangc run must not need a thread-pool thread. It once did, three ways: the stdout and
/// stderr reads each held one for the child's life, and the timeout was a pool timer. On a busy
/// pool (a host compiling many shaders at once, a loaded 4-core CI runner) the output was not
/// drained, slangc blocked on a full pipe until its 60 s limit, and the kill itself arrived
/// late: with the pool saturated as below, a 2 s timeout took 216 s. That is how
/// <c>SlangcTimeoutTests</c> and unrelated Slang compiles failed intermittently on
/// <c>windows-latest</c>.
/// </summary>
[Collection(nameof(SaturatedThreadPoolCollection))]
public sealed class SlangcThreadPoolIndependenceTests
{
    /// <summary>Queues far more blocked work than the pool will add threads for while a test
    /// runs (it adds about one or two a second), so nothing new gets a pool thread until the
    /// returned event is set.</summary>
    private static ManualResetEventSlim SaturateThreadPool()
    {
        var release = new ManualResetEventSlim(false);
        for (int i = 0; i < 400; i++)
            ThreadPool.QueueUserWorkItem(_ => release.Wait(TimeSpan.FromSeconds(120)));

        // Proof the pool really is saturated, or the tests below would prove nothing.
        var ran = new ManualResetEventSlim(false);
        ThreadPool.QueueUserWorkItem(_ => ran.Set());
        if (ran.Wait(TimeSpan.FromMilliseconds(500)))
        {
            release.Set();
            throw new InvalidOperationException("The thread pool still had a free thread: the test would prove nothing.");
        }

        return release;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunSlangc_TimesOutOnTime_WhenTheThreadPoolHasNoFreeThread()
    {
        (string path, string[] arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "ping", "-n", "1000", "127.0.0.1" })
            : ("/bin/sh", new[] { "-c", "sleep 1000" });
        // Larger than the pipe buffer, to a child that never reads it: the write blocks too.
        string bigSource = new string('/', 1 << 20) + "\n";

        ManualResetEventSlim release = SaturateThreadPool();
        try
        {
            var clock = Stopwatch.StartNew();

            var ex = Should.Throw<TimeoutException>(() =>
                SlangCompiler.RunSlangc(path, Environment.CurrentDirectory, bigSource, arguments,
                    TimeSpan.FromSeconds(2), CancellationToken.None));

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
            ex.Message.ShouldContain("did not exit within 2 s", Case.Sensitive);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RunSlangc_ReturnsAllOfTheChildsOutput_WhenTheThreadPoolHasNoFreeThread()
    {
        // Far more output than a pipe buffer holds, on both streams, stderr first: an undrained
        // pipe blocks the child, so it would never exit, and a reader that finished stdout
        // before starting stderr would wait for ever too.
        var lines = new StringBuilder();
        for (int i = 0; i < 20_000; i++)
            lines.Append("line ").Append(i).Append('\n');
        string expected = lines.ToString();
        string file = Path.Combine(Path.GetTempPath(), "shadowdusk_slangc_pool_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, expected);

        (string path, string[] arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "type", file, "1>&2", "&", "type", file })
            : ("/bin/sh", new[] { "-c", $"cat '{file}' 1>&2; cat '{file}'" });

        try
        {
            ManualResetEventSlim release = SaturateThreadPool();
            try
            {
                var clock = Stopwatch.StartNew();

                (int exitCode, string stdout, string stderr) = SlangCompiler.RunSlangc(
                    path, Environment.CurrentDirectory, "ignored\n", arguments, TimeSpan.FromSeconds(60), CancellationToken.None);

                clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
                exitCode.ShouldBe(0);
                (stdout == expected).ShouldBeTrue("stdout was not the file's lines, in order and complete");
                (stderr == expected).ShouldBeTrue("stderr was not the file's lines, in order and complete");
            }
            finally
            {
                release.Set();
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
