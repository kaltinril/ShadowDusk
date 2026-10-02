#nullable enable

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ShadowDusk.Integration.Tests;

/// <summary>What a child process left behind when it exited on its own.</summary>
internal sealed record ChildProcessResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed)
{
    /// <summary>stdout followed by stderr, for callers that only log or search the text.</summary>
    public string Output => Stdout + Stderr;
}

/// <summary>
/// A child process was still running (or still holding its output pipes) when its time ran
/// out. The message is the evidence: elapsed time, command line, what the process was doing,
/// and everything it had written so far.
/// </summary>
internal sealed class ChildProcessTimeoutException : TimeoutException
{
    public ChildProcessTimeoutException(
        string message, int processId, TimeSpan elapsed, string stdout, string stderr)
        : base(message)
    {
        ProcessId = processId;
        Elapsed = elapsed;
        Stdout = stdout;
        Stderr = stderr;
    }

    public int ProcessId { get; }
    public TimeSpan Elapsed { get; }
    public string Stdout { get; }
    public string Stderr { get; }
}

/// <summary>
/// The one way a test runs a child process (issue #316). Every test helper used to carry its
/// own copy of this, and the copies shared two defects: several read stdout to the end and
/// only then stderr, so a child that filled the stderr pipe first blocked forever; and on
/// timeout they reported a bare <see cref="OperationCanceledException"/>, throwing away the
/// output and leaving the child running.
/// </summary>
/// <remarks>
/// <para>stdout and stderr are drained concurrently from the moment the process starts, each
/// on its own thread. Dedicated threads, not <c>ReadToEndAsync</c>: a redirected pipe is a
/// synchronous handle on Windows, so each async read parks a thread-pool thread for the
/// child's whole lifetime, and a test host running many of these at once is exactly where the
/// pool is short. The timeout path must not depend on the thing that may be starved.</para>
/// <para>On timeout the whole process tree is killed and a
/// <see cref="ChildProcessTimeoutException"/> carries the elapsed time, the command line, the
/// child's CPU time and thread states (a blocked child has used almost none; a starved or busy
/// one has), and the stdout/stderr captured so far.</para>
/// </remarks>
internal static class ChildProcess
{
    /// <summary>How long a killed process gets to disappear before the report says it did not.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(15);

    /// <summary>How long the pipes get to reach EOF after the kill (a detached descendant can hold them).</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>Per-stream cap in a timeout message; the tail is kept because it is the most recent.</summary>
    private const int MaxReportedChars = 32 * 1024;

    /// <summary>
    /// Starts <paramref name="startInfo"/> (stdout and stderr are always redirected), waits for
    /// it to exit and for both pipes to reach EOF, and returns the exit code and the text.
    /// </summary>
    /// <param name="startInfo">File, arguments, working directory, environment.</param>
    /// <param name="timeout">Budget for the whole run, start to EOF.</param>
    /// <param name="label">Names the process in a failure message; defaults to the file name.</param>
    /// <param name="standardInput">Text written to the child's stdin, which is then closed.</param>
    /// <param name="captureHangEvidence">
    /// Before killing a timed-out child, also take native stacks and a dump of it
    /// (<see cref="HangDiagnostics"/>). Costs up to a minute or two, so it is for .NET children
    /// whose hang would otherwise be undiagnosable.
    /// </param>
    /// <param name="cancellationToken">
    /// A caller's own deadline. Cancelling it is reported exactly like the timeout, with the
    /// same evidence, because in a test a cancelled token is a deadline.
    /// </param>
    /// <exception cref="ChildProcessTimeoutException">The child outlived its budget; it has been killed.</exception>
    public static async Task<ChildProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string? label = null,
        string? standardInput = null,
        bool captureHangEvidence = false,
        CancellationToken cancellationToken = default)
    {
        using var session = Session.Start(startInfo, label, standardInput);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await session.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);

            // Exit only proves the process is gone. A descendant that inherited the pipes
            // keeps them open, so reaching EOF is held to the same deadline.
            await session.PipesClosed.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            string reason = cancellationToken.IsCancellationRequested
                ? "was still running when the caller's deadline was cancelled"
                : $"did not finish within its {Seconds(timeout)} s timeout";
            TimeSpan observed = session.Clock.Elapsed;
            string state = session.DescribeProcess();

            string evidence = "";
            if (captureHangEvidence && !session.HasExited())
                evidence = await HangDiagnostics.CaptureAsync(session.Process.Id, session.Label).ConfigureAwait(false);

            string killNote = session.KillTree();
            bool exited = await Completes(session.Process.WaitForExitAsync(CancellationToken.None), KillGrace).ConfigureAwait(false);
            bool drained = await Completes(session.PipesClosed, DrainGrace).ConfigureAwait(false);

            throw session.Timeout(reason, timeout, observed, state, killNote, exited, drained, evidence);
        }

        return session.Result();
    }

    /// <summary>
    /// The blocking form of <see cref="RunAsync"/>, for the one place a product seam is
    /// synchronous by contract (the Slang in-process compile delegate). It blocks on the
    /// process handle and on the reader threads directly, never on a task. Use
    /// <see cref="RunAsync"/> everywhere else.
    /// </summary>
    public static ChildProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string? label = null,
        string? standardInput = null)
    {
        using var session = Session.Start(startInfo, label, standardInput);

        if (session.Process.WaitForExit(timeout) && session.JoinPipes(timeout - session.Clock.Elapsed))
            return session.Result();

        TimeSpan observed = session.Clock.Elapsed;
        string state = session.DescribeProcess();
        string killNote = session.KillTree();
        bool exited = session.Process.WaitForExit(KillGrace);
        bool drained = session.JoinPipes(DrainGrace);

        throw session.Timeout(
            $"did not finish within its {Seconds(timeout)} s timeout", timeout, observed, state, killNote, exited, drained, evidence: "");
    }

    /// <summary>The command line as a human would retype it (used in failure messages).</summary>
    public static string CommandLine(ProcessStartInfo startInfo)
    {
        string arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(" ", startInfo.ArgumentList.Select(Quote))
            : startInfo.Arguments;
        return arguments.Length == 0 ? Quote(startInfo.FileName) : Quote(startInfo.FileName) + " " + arguments;

        static string Quote(string argument) =>
            argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;
    }

    private static async Task<bool> Completes(Task task, TimeSpan within)
    {
        try
        {
            await task.WaitAsync(within).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One started child: the process, its clock, and the threads draining its pipes.</summary>
    private sealed class Session : IDisposable
    {
        private readonly ProcessStartInfo _startInfo;
        private readonly TimeSpan _startCost;
        private readonly PipeThread _stdout;
        private readonly PipeThread _stderr;
        private readonly PipeThread? _stdin;

        private Session(
            ProcessStartInfo startInfo, string label, Process process, Stopwatch clock, TimeSpan startCost, string? standardInput)
        {
            _startInfo = startInfo;
            _startCost = startCost;
            Label = label;
            Process = process;
            Clock = clock;

            _stdout = PipeThread.Reading(process.StandardOutput, label + " stdout");
            _stderr = PipeThread.Reading(process.StandardError, label + " stderr");
            _stdin = standardInput is null ? null : PipeThread.Writing(process.StandardInput, standardInput, label + " stdin");

            PipesClosed = Task.WhenAll(
                _stdin is null ? [_stdout.Finished, _stderr.Finished] : [_stdout.Finished, _stderr.Finished, _stdin.Finished]);
        }

        public string Label { get; }
        public Process Process { get; }
        public Stopwatch Clock { get; }

        /// <summary>Completes when stdout and stderr have reached EOF (and stdin has been written).</summary>
        public Task PipesClosed { get; }

        public static Session Start(ProcessStartInfo startInfo, string? label, string? standardInput)
        {
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = standardInput is not null;

            string name = label ?? Path.GetFileName(startInfo.FileName);
            var clock = Stopwatch.StartNew();
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{name}: failed to start {CommandLine(startInfo)}");
            return new Session(startInfo, name, process, clock, clock.Elapsed, standardInput);
        }

        public ChildProcessResult Result() =>
            new(Process.ExitCode, _stdout.Text(), _stderr.Text(), Clock.Elapsed);

        public bool JoinPipes(TimeSpan within)
        {
            var remaining = Stopwatch.StartNew();
            foreach (PipeThread? pipe in new PipeThread?[] { _stdout, _stderr, _stdin })
            {
                TimeSpan left = within - remaining.Elapsed;
                if (pipe is not null && !pipe.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                    return false;
            }
            return true;
        }

        public bool HasExited()
        {
            try
            {
                return Process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return true;
            }
        }

        public string KillTree()
        {
            try
            {
                Process.Kill(entireProcessTree: true);
                return "process tree killed";
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                return "process tree kill failed: " + ex.Message;
            }
        }

        /// <summary>
        /// What the child was doing when its time ran out, read BEFORE the kill. CPU time against
        /// wall time is the cheap discriminator the old helpers never recorded: a child blocked on
        /// a lock or a pipe has used almost none, a starved or genuinely slow one has.
        /// </summary>
        public string DescribeProcess()
        {
            var sb = new StringBuilder();
            try
            {
                Process.Refresh();
                if (Process.HasExited)
                {
                    sb.Append("process ").Append(Process.Id).Append(" had already exited (code ").Append(Process.ExitCode)
                      .Append("), so a descendant that inherited its stdout/stderr is still holding them open");
                    return sb.ToString();
                }

                sb.Append("process ").Append(Process.Id).Append(" still running: CPU ")
                  .Append(Seconds(Process.TotalProcessorTime)).Append(" s (user ")
                  .Append(Seconds(Process.UserProcessorTime)).Append(" s, kernel ")
                  .Append(Seconds(Process.PrivilegedProcessorTime)).Append(" s), working set ")
                  .Append(Process.WorkingSet64 / (1024 * 1024)).Append(" MB, ")
                  .Append(Process.Threads.Count).Append(" threads");

                foreach (ProcessThread thread in Process.Threads.Cast<ProcessThread>().Take(64))
                    sb.AppendLine().Append("  thread ").Append(DescribeThread(thread));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process exited between two reads, or this OS does not expose the field.
                sb.Append(" (process state unreadable: ").Append(ex.Message).Append(')');
            }
            return sb.ToString();
        }

        private static string DescribeThread(ProcessThread thread)
        {
            try
            {
                string state = thread.ThreadState.ToString();
                if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                    state += "/" + thread.WaitReason;
                return $"{thread.Id}: {state}, CPU {Seconds(thread.TotalProcessorTime)} s";
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return $"{thread.Id}: unreadable ({ex.Message})";
            }
        }

        public ChildProcessTimeoutException Timeout(
            string reason, TimeSpan timeout, TimeSpan observed, string state, string killNote, bool exited, bool drained, string evidence)
        {
            string stdout = _stdout.Text();
            string stderr = _stderr.Text();

            var sb = new StringBuilder();
            sb.Append(Label).Append(' ').Append(reason).Append(": ").Append(Seconds(observed)).Append(" s elapsed (")
              .Append(Seconds(_startCost)).AppendLine(" s of it inside Process.Start).");

            // A deadline observed long after it was due means the TEST HOST could not run the
            // continuation: the child may be innocent.
            TimeSpan late = observed - timeout;
            if (late > TimeSpan.FromSeconds(2))
                sb.Append("The ").Append(Seconds(timeout)).Append(" s deadline was noticed ").Append(Seconds(late))
                  .AppendLine(" s late, which points at a starved test host rather than at the child.");

            sb.Append("command: ").AppendLine(CommandLine(_startInfo));
            sb.Append("working directory: ").AppendLine(
                string.IsNullOrEmpty(_startInfo.WorkingDirectory) ? Environment.CurrentDirectory : _startInfo.WorkingDirectory);
            sb.AppendLine(state);
            sb.Append("test host: ").Append(ThreadPool.ThreadCount).Append(" thread-pool threads, ")
              .Append(ThreadPool.PendingWorkItemCount).Append(" queued work items, ")
              .Append(Environment.ProcessorCount).AppendLine(" logical cores");
            sb.Append("after the timeout: ").Append(killNote)
              .Append(exited ? "; the process is gone" : "; THE PROCESS IS STILL ALIVE")
              .AppendLine(drained ? "; both pipes reached EOF" : "; a pipe is still open (a surviving descendant holds it)");
            AppendStream(sb, "stdout", stdout);
            AppendStream(sb, "stderr", stderr);
            if (evidence.Length > 0)
                sb.AppendLine(evidence);

            return new ChildProcessTimeoutException(sb.ToString(), Process.Id, observed, stdout, stderr);
        }

        private static void AppendStream(StringBuilder sb, string name, string text)
        {
            if (text.Length == 0)
            {
                sb.Append("--- ").Append(name).AppendLine(" so far: (nothing)");
                return;
            }

            sb.Append("--- ").Append(name).Append(" so far (").Append(text.Length).Append(" chars");
            if (text.Length > MaxReportedChars)
                sb.Append(", last ").Append(MaxReportedChars).Append(" shown");
            sb.AppendLine("):");
            sb.AppendLine(text.Length > MaxReportedChars ? text[^MaxReportedChars..] : text);
        }

        public void Dispose() => Process.Dispose();
    }

    /// <summary>
    /// A background thread that drains one redirected output pipe into memory, or feeds stdin.
    /// The text read so far is available at any moment, which is what lets a timeout report it.
    /// </summary>
    private sealed class PipeThread
    {
        private readonly StringBuilder _text = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        private PipeThread(Action<PipeThread> body, string name)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    body(this);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The pipe went away under the call (the child was killed, or exited without
                    // reading its stdin). Whatever arrived before that is kept.
                }
                finally
                {
                    _finished.TrySetResult();
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
            _thread.Start();
        }

        public Task Finished => _finished.Task;

        public static PipeThread Reading(StreamReader reader, string name) => new(
            self =>
            {
                var buffer = new char[4096];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (self._text)
                        self._text.Append(buffer, 0, read);
                }
            },
            name);

        public static PipeThread Writing(StreamWriter writer, string text, string name) => new(
            _ =>
            {
                try
                {
                    writer.Write(text);
                }
                finally
                {
                    writer.Close();
                }
            },
            name);

        public bool Join(TimeSpan within) => _thread.Join(within);

        public string Text()
        {
            lock (_text)
                return _text.ToString();
        }
    }
}
