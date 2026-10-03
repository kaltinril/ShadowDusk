#nullable enable

using Shouldly;
using ShadowDusk.ImageTests.GlContext;
using Xunit;

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Pure unit tests for <see cref="GlContextGate"/> (issue #345): a native
/// make-current failure must fail the test that hit it and every later one,
/// and must never leave a later test blocked. No GL, no I/O; the native calls
/// are injected. Every wait is bounded, so a regression fails these tests
/// instead of hanging them.
/// </summary>
public sealed class GlContextGateTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static GlContextGate NewGate(
        Action? makeCurrent = null,
        Action? clearCurrent = null,
        TimeSpan? acquireTimeout = null,
        TimeSpan? holdLimit = null,
        Action<string>? onHoldLimitExceeded = null) =>
        new(
            makeCurrent ?? (() => { }),
            clearCurrent ?? (() => { }),
            acquireTimeout ?? Bound,
            holdLimit ?? TimeSpan.FromMinutes(10),
            onHoldLimitExceeded ?? (_ => { }));

    [Fact]
    public async Task MakeCurrentFailure_FailsTheTest_AndTheNextAcquireOnAnotherThreadFailsFastInsteadOfBlocking()
    {
        // The issue #345 hang: the old fixture threw out of make-current while still
        // holding its lock, and the next test (on another thread) waited forever.
        var nativeError = new InvalidOperationException(
            "PlatformError: WGL: Failed to make context current: The handle is invalid.");
        using var gate = NewGate(makeCurrent: () => throw nativeError);

        var first = Should.Throw<GlContextLostException>(() => gate.Acquire());
        first.InnerException.ShouldBeSameAs(nativeError);
        gate.IsLost.ShouldBeTrue();

        Task<GlContextLostException> next = Task.Factory.StartNew(
            () => Should.Throw<GlContextLostException>(() => gate.Acquire()),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        GlContextLostException second = await next.WaitAsync(Bound);
        second.Message.ShouldContain("The handle is invalid.", Case.Sensitive,
            "a later test must fail with the ORIGINAL native error, verbatim");
        second.InnerException.ShouldBeSameAs(nativeError);
    }

    [Fact]
    public void MakeCurrentFailure_MessageKeepsTheNativeTextAndTheCallerDiagnostics()
    {
        using var gate = NewGate(makeCurrent: () => throw new InvalidOperationException("WGL: native text"));

        var ex = Should.Throw<GlContextLostException>(
            () => gate.Acquire(() => "Window handle 0x1234 still valid: False."));

        ex.Message.ShouldContain("WGL: native text", Case.Sensitive);
        ex.Message.ShouldContain("Window handle 0x1234 still valid: False.", Case.Sensitive);
        ex.Message.ShouldContain("#345", Case.Sensitive);
    }

    [Fact]
    public void LostGate_DoesNotCallMakeCurrentAgain()
    {
        int calls = 0;
        using var gate = NewGate(makeCurrent: () =>
        {
            calls++;
            throw new InvalidOperationException("dead window");
        });

        Should.Throw<GlContextLostException>(() => gate.Acquire());
        Should.Throw<GlContextLostException>(() => gate.Acquire());
        Should.Throw<GlContextLostException>(() => gate.Acquire());

        calls.ShouldBe(1, "a lost context fails later tests at once instead of re-hammering a dead window");
    }

    [Fact]
    public void Lease_MakesCurrentOnAcquire_AndClearsOnDispose_ThenTheNextAcquireSucceeds()
    {
        int made = 0, cleared = 0;
        using var gate = NewGate(makeCurrent: () => made++, clearCurrent: () => cleared++);

        using (gate.Acquire())
        {
            made.ShouldBe(1);
            cleared.ShouldBe(0);
        }

        cleared.ShouldBe(1);
        using (gate.Acquire())
            made.ShouldBe(2);
        cleared.ShouldBe(2);
        gate.IsLost.ShouldBeFalse();
    }

    [Fact]
    public void Lease_DisposedTwice_ReleasesOnce()
    {
        int cleared = 0;
        using var gate = NewGate(clearCurrent: () => cleared++);

        IDisposable lease = gate.Acquire();
        lease.Dispose();
        lease.Dispose();

        cleared.ShouldBe(1);
        using (gate.Acquire()) { }
        cleared.ShouldBe(2);
    }

    [Fact]
    public async Task Acquire_WhileAnotherTestHoldsTheContext_TimesOutInsteadOfBlocking()
    {
        using var gate = NewGate(acquireTimeout: TimeSpan.FromMilliseconds(200));
        using IDisposable held = gate.Acquire();

        Task<GlContextLostException> waiter = Task.Factory.StartNew(
            () => Should.Throw<GlContextLostException>(() => gate.Acquire()),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        GlContextLostException ex = await waiter.WaitAsync(Bound);
        ex.Message.ShouldContain("Timed out", Case.Sensitive);
        gate.IsLost.ShouldBeFalse("a slow holder is not a lost context");
    }

    [Fact]
    public void ClearFailure_ReleasesTheGate_AndMarksTheContextLost()
    {
        using var gate = NewGate(clearCurrent: () => throw new InvalidOperationException("clear failed"));

        IDisposable lease = gate.Acquire();
        Should.Throw<InvalidOperationException>(() => lease.Dispose());

        gate.IsLost.ShouldBeTrue();
        var next = Should.Throw<GlContextLostException>(() => gate.Acquire());
        next.Message.ShouldContain("clear failed", Case.Sensitive);
    }

    [Fact]
    public async Task HoldLimit_Exceeded_RunsTheWatchdogWithADiagnostic()
    {
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var gate = NewGate(
            holdLimit: TimeSpan.FromMilliseconds(100),
            onHoldLimitExceeded: message => fired.TrySetResult(message));

        using IDisposable lease = gate.Acquire();

        string message = await fired.Task.WaitAsync(Bound);
        message.ShouldContain("held the shared GL context", Case.Sensitive);
        message.ShouldContain("#345", Case.Sensitive);
    }
}
