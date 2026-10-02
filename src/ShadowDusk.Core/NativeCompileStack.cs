#nullable enable

using System.Runtime.ExceptionServices;

namespace ShadowDusk.Core;

/// <summary>
/// Runs a native compiler call (DXC, vkd3d-shader, SPIRV-Cross) on a ShadowDusk-owned thread
/// with a large, explicit stack, so a valid but extremely deep shader cannot overflow the
/// caller's stack and take the host process down (issue #306).
/// </summary>
/// <remarks>
/// <para>
/// The native compilers recurse once per nesting level of the source (a term of an additive
/// chain, an <c>else if</c>, a call in a call chain). On the caller's thread that recursion ran
/// on whatever stack the host happened to give it: 1.5 MB for the .NET host's threads on
/// Windows, where a 2,168-term chain died inside <c>dxcompiler.dll</c> with
/// <c>0xC00000FD</c>. A native stack overflow is not an exception: nothing can catch it, the
/// process just exits. So the call runs here instead, on a stack of
/// <see cref="StackSizeBytes"/>, which is address space only: pages are committed as the
/// recursion actually touches them, so an ordinary shader costs what it cost before.
/// </para>
/// <para>
/// <b>What moves and what does not.</b> Only the delegate passed to <see cref="Run{T}"/> runs
/// on the worker. The native compiler wrappers keep it down to the native call itself, so
/// through the synchronous <c>Compile</c> the pipeline and every consumer callback stay on
/// the calling thread; <c>EffectCompiler.CompileAsync</c>, whose caller is already on a
/// thread-pool thread, hands the whole pipeline over in one call and the native calls inside
/// run inline (a nested <see cref="Run{T}"/> on a worker never hops again). The calling
/// thread blocks until the worker is done, exactly as it blocked inside the P/Invoke before,
/// so every lock, gate and ordering guarantee the caller holds around the call still holds
/// (<c>DxcSignalIsolation</c>'s one-time serialized prime, and <c>DxcForkGate</c>, which is
/// entered inside the delegate and therefore on the worker, the thread that is in
/// <c>setlocale</c>). The caller's <see cref="ExecutionContext"/> flows to the worker for the
/// duration of the call. The bytes a compiler emits do not depend on the thread it runs on;
/// the corpus is byte-identical with <see cref="Enabled"/> on and off.
/// </para>
/// <para>
/// <b>Cost.</b> Workers are reused: an idle worker takes the next call (a few microseconds
/// of handoff when idle, about 0.1 ms on a real compile once the work has changed core,
/// against roughly 100 microseconds just to start a thread), a new one is started only when
/// every existing worker is busy, so concurrent compiles stay concurrent, and a worker that
/// has had nothing to do for <see cref="IdleTimeout"/> exits, which also returns whatever
/// stack a deep compile committed. Workers are background threads and never keep a process
/// alive. Measured on a 10 ms effect: no difference through <c>CompileAsync</c>, about
/// 0.4 ms through the synchronous <c>Compile</c> (four to six native calls).
/// </para>
/// <para>
/// <b>Not a guarantee.</b> Source deep enough to exhaust this stack still kills the process;
/// the measured ceilings are in <c>project_facts.md</c>. If the host cannot start the thread
/// (no address space for the stack), the call runs on the caller's thread as it always did.
/// </para>
/// </remarks>
internal static class NativeCompileStack
{
    /// <summary>
    /// Stack reserved for a worker: 64 MB, about 43 times the 1.5 MB the .NET host gives its
    /// threads on Windows and 8 times the usual 8 MB on Linux and macOS.
    /// </summary>
    internal const int StackSizeBytes = 64 * 1024 * 1024;

    /// <summary>How long a worker waits for another call before it exits.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(5);

    private static readonly object IdleLock = new();
    private static readonly List<Worker> Idle = [];

    private static volatile bool _enabled = true;
    private static int _workersStarted;

    [ThreadStatic]
    private static bool t_isWorker;

    /// <summary>
    /// Test seam: <see langword="false"/> runs every call on the caller's thread, the behaviour
    /// before issue #306. The product never turns it off.
    /// </summary>
    internal static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>True on a worker thread, where a nested <see cref="Run{T}"/> runs inline.</summary>
    internal static bool IsWorkerThread => t_isWorker;

    /// <summary>How many worker threads this process has started so far (reuse shows as a low number).</summary>
    internal static int WorkersStarted => Volatile.Read(ref _workersStarted);

    /// <summary>
    /// Runs <paramref name="nativeCall"/> on a large-stack worker and returns its result. Blocks
    /// the calling thread until it completes; an exception it throws is rethrown here with its
    /// original stack trace.
    /// </summary>
    internal static T Run<T>(Func<T> nativeCall)
    {
        ArgumentNullException.ThrowIfNull(nativeCall);

        if (!_enabled || t_isWorker || !ThreadsAreAvailable)
            return nativeCall();

        Worker? worker = Rent();
        if (worker is null)
            return nativeCall();

        T result = default!;
        try
        {
            worker.Execute(() => result = nativeCall());
        }
        finally
        {
            Return(worker);
        }

        return result;
    }

    /// <summary>
    /// The browser's single-threaded runtime cannot start threads; its compilers are separate
    /// WebAssembly modules with their own stack setting (issue #271), so nothing there calls
    /// this anyway.
    /// </summary>
    private static bool ThreadsAreAvailable => !OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi();

    private static Worker? Rent()
    {
        lock (IdleLock)
        {
            if (Idle.Count > 0)
            {
                // Most recently used first: its stack pages are already committed and warm, and
                // the least recently used ones get to reach their idle timeout.
                Worker reused = Idle[^1];
                Idle.RemoveAt(Idle.Count - 1);
                return reused;
            }
        }

        var worker = new Worker();
        if (!worker.TryStart())
            return null;

        Interlocked.Increment(ref _workersStarted);
        return worker;
    }

    private static void Return(Worker worker)
    {
        lock (IdleLock)
            Idle.Add(worker);
    }

    private static bool TryRetire(Worker worker)
    {
        lock (IdleLock)
            return Idle.Remove(worker);
    }

    private sealed class Worker
    {
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly ManualResetEventSlim _done = new(false);
        private Action? _work;
        private ExecutionContext? _context;
        private ExceptionDispatchInfo? _fault;

        public bool TryStart()
        {
            var thread = new Thread(Loop, StackSizeBytes)
            {
                IsBackground = true,
                Name = "ShadowDusk native compile",
            };

            try
            {
                // UnsafeStart: the worker outlives this call, so it must not inherit the first
                // caller's ExecutionContext. Each call's own context is applied in Loop.
                thread.UnsafeStart();
                return true;
            }
            catch (OutOfMemoryException)
            {
                // No address space (or commit, on a strict-overcommit host) for the stack.
                return false;
            }
        }

        /// <summary>Hands one call to the worker and blocks until it has run.</summary>
        public void Execute(Action work)
        {
            _fault = null;
            _context = ExecutionContext.Capture();
            _work = work;
            _done.Reset();
            _wake.Release();

            // A synchronous handoff to our own thread, not a wait on a Task: the caller is a
            // synchronous compile and was blocked inside this same native call before. Like
            // that P/Invoke, the wait cannot be cut short by Thread.Interrupt: the worker is
            // still inside the native call and must not be handed to anyone else. The interrupt
            // is re-asserted once the call has returned.
            bool interrupted = false;
            while (true)
            {
                try
                {
                    _done.Wait();
                    break;
                }
                catch (ThreadInterruptedException)
                {
                    interrupted = true;
                }
            }

            if (interrupted)
                Thread.CurrentThread.Interrupt();

            _fault?.Throw();
        }

        private void Loop()
        {
            t_isWorker = true;

            while (true)
            {
                if (!_wake.Wait(IdleTimeout))
                {
                    // Idle for the whole timeout. Leave, unless a caller took this worker off
                    // the idle list in the same instant: then its wake-up is already on the way.
                    if (TryRetire(this))
                    {
                        _wake.Dispose();
                        _done.Dispose();
                        return;
                    }

                    _wake.Wait();
                }

                Action work = _work!;
                ExecutionContext? context = _context;
                _work = null;
                _context = null;

                try
                {
                    if (context is null)
                        work();
                    else
                        ExecutionContext.Run(context, static state => ((Action)state!)(), work);
                }
                catch (Exception ex)
                {
                    _fault = ExceptionDispatchInfo.Capture(ex);
                }

                _done.Set();
            }
        }
    }
}
