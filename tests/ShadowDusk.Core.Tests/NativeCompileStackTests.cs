#nullable enable

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// <see cref="NativeCompileStack"/> carries every native compiler call (issue #306). These pin
/// the handoff contract: the call runs on a worker with the large stack, its result and its
/// exceptions come back to the caller, the caller's context flows, nested calls run inline,
/// workers are reused and never serialize concurrent callers.
/// </summary>
public sealed class NativeCompileStackTests
{
    [Fact]
    public void Run_ReturnsTheResult_FromAnotherThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;

        (int value, int thread, bool onWorker) = NativeCompileStack.Run(() =>
            (42, Environment.CurrentManagedThreadId, NativeCompileStack.IsWorkerThread));

        value.ShouldBe(42);
        thread.ShouldNotBe(callerThread);
        onWorker.ShouldBeTrue();
        NativeCompileStack.IsWorkerThread.ShouldBeFalse();
    }

    [Fact]
    public void Run_Nested_RunsInlineOnTheSameWorker()
    {
        (int outer, int inner) = NativeCompileStack.Run(() =>
            (Environment.CurrentManagedThreadId, NativeCompileStack.Run(() => Environment.CurrentManagedThreadId)));

        inner.ShouldBe(outer);
    }

    [Fact]
    public void Run_RethrowsTheWorkersException_WithItsTypeAndMessage()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            NativeCompileStack.Run<int>(() => throw new InvalidOperationException("from the worker")));

        ex.Message.ShouldBe("from the worker");
        ex.StackTrace.ShouldNotBeNull();
        ex.StackTrace.ShouldContain(nameof(Run_RethrowsTheWorkersException_WithItsTypeAndMessage), Case.Sensitive);

        // The worker survives its caller's exception and takes the next call.
        NativeCompileStack.Run(() => 7).ShouldBe(7);
    }

    [Fact]
    public void Run_FlowsTheCallersExecutionContext()
    {
        var local = new AsyncLocal<string?>();
        local.Value = "caller's value";

        NativeCompileStack.Run(() => local.Value).ShouldBe("caller's value");
    }

    [Fact]
    public void Run_SequentialCalls_ReuseOneWorker()
    {
        int[] threads = Enumerable.Range(0, 20)
            .Select(_ => NativeCompileStack.Run(() => Environment.CurrentManagedThreadId))
            .ToArray();

        // Most recently used first: back-to-back calls from one thread land on one worker.
        threads.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void Run_ConcurrentCallers_RunAtTheSameTime()
    {
        const int callers = 6;
        using var allInside = new Barrier(callers);
        bool[] met = new bool[callers];

        // If the calls were serialized on one worker, no participant could reach the barrier
        // while another is inside it, and the wait would time out. Dedicated caller threads,
        // so the thread pool's injection rate plays no part.
        Thread[] threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
            met[i] = NativeCompileStack.Run(() => allInside.SignalAndWait(TimeSpan.FromSeconds(20))))).ToArray();
        foreach (Thread t in threads) t.Start();
        foreach (Thread t in threads) t.Join();

        met.ShouldAllBe(m => m);
    }

    [Fact]
    public void Run_Disabled_RunsOnTheCallersThread()
    {
        NativeCompileStack.Enabled.ShouldBeTrue();
        try
        {
            NativeCompileStack.Enabled = false;
            NativeCompileStack.Run(() => Environment.CurrentManagedThreadId).ShouldBe(Environment.CurrentManagedThreadId);
            NativeCompileStack.Run(() => NativeCompileStack.IsWorkerThread).ShouldBeFalse();
        }
        finally
        {
            NativeCompileStack.Enabled = true;
        }
    }

    [Fact]
    public void Run_RejectsANullCall()
    {
        Should.Throw<ArgumentNullException>(() => NativeCompileStack.Run<int>(null!));
    }

    /// <summary>
    /// The worker really has the stack it claims: measured through the kernel's own view of the
    /// current thread's stack limits (Windows only; the other OSes prove it functionally in
    /// <c>DeepShaderStackTests</c>).
    /// </summary>
    [WindowsFact]
    public void Worker_HasAtLeastTheReservedStack()
    {
        (nuint callerStack, nuint workerStack) = (CurrentThreadStackReserve(), NativeCompileStack.Run(CurrentThreadStackReserve));

        workerStack.ShouldBeGreaterThanOrEqualTo((nuint)NativeCompileStack.StackSizeBytes);
        callerStack.ShouldBeLessThan(workerStack, "the test host's own thread already had a stack this large, so this proves nothing");
    }

    private static nuint CurrentThreadStackReserve()
    {
        GetCurrentThreadStackLimits(out nuint low, out nuint high);
        return high - low;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetCurrentThreadStackLimits(out nuint lowLimit, out nuint highLimit);
}

/// <summary>A fact that runs on Windows and is reported as skipped (never as passed) elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Reads the thread's stack limits through kernel32.";
    }
}
