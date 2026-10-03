#nullable enable

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// Thrown when the shared GL context could not be claimed: the native
/// make-current failed (this run, or an earlier test in it), or another
/// test held the context past <see cref="GlContextGate"/>'s acquire timeout.
/// The test that hits it FAILS; it never blocks the host (issue #345).
/// </summary>
public sealed class GlContextLostException : Exception
{
    public GlContextLostException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// The exclusive-use gate in front of <see cref="GlContextFixture"/>'s one GL
/// context. Pure logic (the native calls are injected), so the issue #345
/// guarantees are unit-tested without a GPU:
/// <list type="number">
///   <item>A make-current failure RELEASES the gate and is rethrown, so the
///         test that hit it fails and the next test does not block. The
///         original fixture took a <c>Monitor</c> and threw out of
///         make-current while holding it, so every later test on another
///         thread waited on that monitor forever and the host never exited.</item>
///   <item>After one make-current failure the gate stays lost: every later
///         acquire fails at once, carrying the original error, instead of
///         re-hammering a dead window.</item>
///   <item>Acquiring is bounded (<c>acquireTimeout</c>): a holder that never
///         releases fails the waiter with a <see cref="GlContextLostException"/>
///         instead of hanging it.</item>
///   <item>Holding is watched (<c>holdLimit</c>): if a lease is not released in
///         time (a native GL call that never returns), <c>onHoldLimitExceeded</c>
///         runs. The fixture passes <see cref="Environment.FailFast(string)"/>, so
///         the host exits and the run fails instead of hanging until someone
///         kills it.</item>
/// </list>
/// The gate is a <see cref="SemaphoreSlim"/>, not a thread-affine lock: a test
/// may claim on one thread and the lease must still release cleanly.
/// </summary>
public sealed class GlContextGate : IDisposable
{
    private readonly Action _makeCurrent;
    private readonly Action _clearCurrent;
    private readonly TimeSpan _acquireTimeout;
    private readonly TimeSpan _holdLimit;
    private readonly Action<string> _onHoldLimitExceeded;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Exception? _lostCause;

    public GlContextGate(
        Action makeCurrent,
        Action clearCurrent,
        TimeSpan acquireTimeout,
        TimeSpan holdLimit,
        Action<string> onHoldLimitExceeded)
    {
        _makeCurrent         = makeCurrent;
        _clearCurrent        = clearCurrent;
        _acquireTimeout      = acquireTimeout;
        _holdLimit           = holdLimit;
        _onHoldLimitExceeded = onHoldLimitExceeded;
    }

    /// <summary><c>true</c> once a make-current has failed; every later acquire fails fast.</summary>
    public bool IsLost => _lostCause is not null;

    /// <summary>
    /// Claims the context for the calling test and makes it current on this
    /// thread. Dispose the returned lease (always via <c>using</c>) to clear
    /// it and let the next test in.
    /// </summary>
    /// <param name="describeFailure">
    /// Extra diagnostics appended to a make-current failure message (for example
    /// whether the window handle is still valid). Called only on failure.
    /// </param>
    public IDisposable Acquire(Func<string>? describeFailure = null)
    {
        ThrowIfLost();

        if (!_gate.Wait(_acquireTimeout))
        {
            throw new GlContextLostException(
                $"Timed out after {_acquireTimeout.TotalSeconds:0} s waiting for the shared GL context: "
                + "another test still holds it. Failing this test instead of blocking the host (issue #345).");
        }

        try
        {
            ThrowIfLost();
            _makeCurrent();
        }
        catch (GlContextLostException)
        {
            _gate.Release();
            throw;
        }
        catch (Exception ex)
        {
            _lostCause = ex;
            _gate.Release();
            string extra = describeFailure is null ? string.Empty : " " + describeFailure();
            throw new GlContextLostException(
                $"Making the shared GL context current failed: {ex.GetType().Name}: {ex.Message.TrimEnd()}{extra} "
                + "The context is now marked lost: every later GL test in this run fails at once "
                + "with this cause instead of blocking the host (issue #345).",
                ex);
        }

        return new Lease(this);
    }

    private void ThrowIfLost()
    {
        Exception? cause = _lostCause;
        if (cause is not null)
        {
            throw new GlContextLostException(
                $"The shared GL context was lost earlier in this run: {cause.GetType().Name}: {cause.Message}",
                cause);
        }
    }

    private void Release()
    {
        try
        {
            _clearCurrent();
        }
        catch (Exception ex)
        {
            // A context that cannot be released is as unusable as one that cannot be
            // claimed: the test that held it fails with this, and later tests fail fast.
            _lostCause ??= ex;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Lease : IDisposable
    {
        private readonly GlContextGate _owner;
        private readonly Timer _watchdog;
        private int _disposed;

        public Lease(GlContextGate owner)
        {
            _owner    = owner;
            _watchdog = new Timer(
                static state => ((Lease)state!).OnHoldLimitExceeded(),
                this,
                owner._holdLimit,
                Timeout.InfiniteTimeSpan);
        }

        private void OnHoldLimitExceeded()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _owner._onHoldLimitExceeded(
                $"[ShadowDusk.ImageTests] A test held the shared GL context for more than "
                + $"{_owner._holdLimit.TotalSeconds:0} s (a GL call that never returned). "
                + "Ending the test host so the run fails instead of hanging (issue #345).");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _watchdog.Dispose();
            _owner.Release();
        }
    }
}
