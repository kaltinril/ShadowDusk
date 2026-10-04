#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ShadowDusk.Core;

/// <summary>
/// Says where a cancelled compile spent its time (issue #373). A <see cref="CancellationToken"/>
/// cannot interrupt a native compiler call, only the gaps between calls, so when a compile is
/// cancelled the useful questions are: how long did it run, which native call was the slow
/// one, did it even start, and what else in the process was inside a native call at the time.
/// Every <see cref="NativeCompileStack.Run{T}(string, Func{T})"/> records its call here, and
/// the compile's entry point turns a cancellation into an
/// <see cref="OperationCanceledException"/> whose message carries the answer.
/// </summary>
/// <remarks>
/// Cost: one <see cref="Stopwatch.GetTimestamp"/> pair and one dictionary add/remove per native
/// call (a compile makes four to six), against calls that take milliseconds.
/// </remarks>
internal sealed class NativeCallTrace
{
    [ThreadStatic]
    private static NativeCallTrace? t_current;

    private static long s_nextCallId;

    /// <summary>Every native call in flight in this process, by call id.</summary>
    private static readonly ConcurrentDictionary<long, InFlightCall> InFlight = new();

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly NativeCallTrace? _outer;
    private readonly TimeSpan _queued;
    private readonly List<(string Name, TimeSpan Duration)> _calls = [];
    private string? _current;
    private long _currentStarted;

    private NativeCallTrace(NativeCallTrace? outer, TimeSpan queued)
    {
        _outer = outer;
        _queued = queued;
    }

    /// <summary>The trace of the compile running on this thread, if any.</summary>
    internal static NativeCallTrace? Current => t_current;

    /// <summary>
    /// Starts tracing the compile that runs on this thread until <see cref="End"/>.
    /// <paramref name="queued"/> is how long the compile waited for a thread before it started
    /// (zero for a synchronous call).
    /// </summary>
    internal static NativeCallTrace Begin(TimeSpan queued)
    {
        var trace = new NativeCallTrace(t_current, queued);
        t_current = trace;
        return trace;
    }

    /// <summary>Stops tracing on this thread, restoring an enclosing trace.</summary>
    internal void End() => t_current = _outer;

    /// <summary>Elapsed time since <see cref="Begin"/>.</summary>
    internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>Records the start of a native call made by this thread; dispose the result when it returns.</summary>
    internal static CallScope Enter(string name)
    {
        long id = Interlocked.Increment(ref s_nextCallId);
        long now = Stopwatch.GetTimestamp();
        InFlight[id] = new InFlightCall(name, now, Environment.CurrentManagedThreadId);

        NativeCallTrace? trace = t_current;
        if (trace is not null)
        {
            trace._current = name;
            trace._currentStarted = now;
        }
        return new CallScope(id, trace, name, now);
    }

    /// <summary>
    /// The native calls in flight anywhere in this process right now, longest first, as text
    /// (for a cancellation message or a stall report). Empty when there are none.
    /// </summary>
    internal static string DescribeInFlight()
    {
        long now = Stopwatch.GetTimestamp();
        var calls = InFlight.Values.OrderBy(c => c.Started).ToList();
        if (calls.Count == 0)
            return "No native compiler call in flight in this process";

        var sb = new StringBuilder();
        sb.Append(calls.Count.ToString(CultureInfo.InvariantCulture))
          .Append(calls.Count == 1 ? " native compiler call" : " native compiler calls")
          .Append(" in flight in this process: ");
        sb.AppendJoin("; ", calls.Select(c =>
            $"{c.Name} for {Seconds(Stopwatch.GetElapsedTime(c.Started, now))} s (managed thread {c.ThreadId})"));
        return sb.ToString();
    }

    /// <summary>
    /// What this compile did before it was cancelled, plus the process load at that moment:
    /// the message of the exception the compile entry point throws.
    /// </summary>
    internal string DescribeCancellation()
    {
        var sb = new StringBuilder();
        sb.Append("The compile was cancelled after ").Append(Seconds(Elapsed)).Append(" s");
        if (_queued > TimeSpan.FromMilliseconds(100))
            sb.Append(", having waited ").Append(Seconds(_queued)).Append(" s for a thread before it started");
        sb.Append(". ");

        if (_calls.Count == 0 && _current is null)
        {
            sb.Append("It was cancelled before its first native compiler call. ");
        }
        else
        {
            TimeSpan native = TimeSpan.FromTicks(_calls.Sum(c => c.Duration.Ticks));
            sb.Append(_calls.Count.ToString(CultureInfo.InvariantCulture)).Append(" native call(s) completed, ")
              .Append(Seconds(native)).Append(" s in total");
            if (_calls.Count > 0)
            {
                (string name, TimeSpan duration) = _calls.MaxBy(c => c.Duration);
                sb.Append("; the longest was ").Append(name).Append(" (").Append(Seconds(duration)).Append(" s)");
                (string lastName, TimeSpan lastDuration) = _calls[^1];
                sb.Append("; the last was ").Append(lastName).Append(" (").Append(Seconds(lastDuration)).Append(" s)");
            }
            sb.Append(". ");
        }

        if (_current is not null)
        {
            sb.Append("Its own ").Append(_current).Append(" call has been in flight for ")
              .Append(Seconds(Stopwatch.GetElapsedTime(_currentStarted))).Append(" s. ");
        }

        sb.Append(SummarizeOthersInFlight(_current is null ? 0 : _currentStarted)).Append(' ');
        sb.Append("Load: ").Append(NativeCompileStack.BusyWorkers.ToString(CultureInfo.InvariantCulture))
          .Append(" native-compile worker(s) busy, ")
          .Append(ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture)).Append(" thread-pool threads, ")
          .Append(ThreadPool.PendingWorkItemCount.ToString(CultureInfo.InvariantCulture)).Append(" queued work items, ")
          .Append(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)).Append(" logical cores.");
        return sb.ToString();
    }

    /// <summary>
    /// The native calls other compiles have in flight, as a count and the oldest one, not a list
    /// (issue #373 review): the message is about THIS compile, the rest is load.
    /// </summary>
    private static string SummarizeOthersInFlight(long ownStarted)
    {
        long now = Stopwatch.GetTimestamp();
        var others = InFlight.Values.Where(c => c.Started != ownStarted).ToList();
        if (others.Count == 0)
            return "No other native compiler call was in flight in this process.";

        InFlightCall oldest = others.MinBy(c => c.Started)!;
        return $"Other compiles had {others.Count.ToString(CultureInfo.InvariantCulture)} native compiler " +
               $"call(s) in flight in this process; the oldest, {oldest.Name}, for " +
               $"{Seconds(Stopwatch.GetElapsedTime(oldest.Started, now))} s.";
    }

    private void Completed(string name, long started, long ended)
    {
        _calls.Add((name, Stopwatch.GetElapsedTime(started, ended)));
        if (ReferenceEquals(_current, name) && _currentStarted == started)
            _current = null;
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record InFlightCall(string Name, long Started, int ThreadId);

    /// <summary>One native call, from <see cref="Enter"/> to dispose.</summary>
    internal readonly struct CallScope : IDisposable
    {
        private readonly long _id;
        private readonly NativeCallTrace? _trace;
        private readonly string _name;
        private readonly long _started;

        internal CallScope(long id, NativeCallTrace? trace, string name, long started)
        {
            _id = id;
            _trace = trace;
            _name = name;
            _started = started;
        }

        public void Dispose()
        {
            InFlight.TryRemove(_id, out _);
            _trace?.Completed(_name, _started, Stopwatch.GetTimestamp());
        }
    }
}
