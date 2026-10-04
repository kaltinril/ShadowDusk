#nullable enable

using System;
using System.Threading;
using Shouldly;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// <see cref="NativeCallTrace"/> is the evidence a cancelled compile carries (issue #373): a
/// token cannot interrupt a native call, so the message has to say which native call took the
/// time, whether the compile ever started, and what else was inside a native call in the
/// process when it gave up.
/// </summary>
[Collection(NativeCompileStackCollection.Name)]
public sealed class NativeCallTraceTests
{
    [Fact]
    public void ANamedCall_IsListedAsInFlight_OnlyWhileItRuns()
    {
        string during = NativeCompileStack.Run("probe call 6d1f", NativeCallTrace.DescribeInFlight);

        during.ShouldContain("probe call 6d1f for ", Case.Sensitive);
        during.ShouldContain("in flight in this process", Case.Sensitive);
        NativeCallTrace.DescribeInFlight().ShouldNotContain("probe call 6d1f", Case.Sensitive);
    }

    [Fact]
    public void ACallThatThrows_LeavesTheInFlightList()
    {
        Should.Throw<InvalidOperationException>(() =>
            NativeCompileStack.Run<int>("throwing call 91ac", () => throw new InvalidOperationException("boom")));

        NativeCallTrace.DescribeInFlight().ShouldNotContain("throwing call 91ac", Case.Sensitive);
    }

    [Fact]
    public void Cancellation_NamesTheCompletedCalls_TheLongestAndTheLast()
    {
        NativeCallTrace trace = NativeCallTrace.Begin(TimeSpan.Zero);
        try
        {
            NativeCompileStack.Run("first native call", () => 1);
            NativeCompileStack.Run("second native call", () => 2);

            string message = trace.DescribeCancellation();

            message.ShouldStartWith("The compile was cancelled after ", Case.Sensitive);
            message.ShouldContain("2 native call(s) completed", Case.Sensitive);
            message.ShouldContain("the longest was ", Case.Sensitive);
            message.ShouldContain("the last was second native call (", Case.Sensitive);
            message.ShouldContain("native-compile worker(s) busy", Case.Sensitive);
            message.ShouldContain("logical cores", Case.Sensitive);
        }
        finally
        {
            trace.End();
        }
    }

    /// <summary>
    /// Review of #376: the message is about THIS compile. Calls other compiles have in flight are
    /// a count and the oldest, never a list, and every sentence starts with a capital.
    /// </summary>
    [Fact]
    public void Cancellation_SummarisesOtherCompilesCalls_AsACountAndTheOldest()
    {
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var other = new Thread(() => NativeCompileStack.Run("other compile call 7b1e", () =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromMinutes(1));
            return 0;
        }));
        other.Start();
        try
        {
            inside.Wait(TimeSpan.FromMinutes(1)).ShouldBeTrue();
            NativeCallTrace trace = NativeCallTrace.Begin(TimeSpan.Zero);
            try
            {
                string message = trace.DescribeCancellation();

                message.ShouldContain("Other compiles had ", Case.Sensitive);
                message.ShouldContain("native compiler call(s) in flight in this process; the oldest, ", Case.Sensitive);
                message.ShouldNotContain("; other compile call 7b1e", Case.Sensitive);
                message.ShouldContain("Load: ", Case.Sensitive);
                message.ShouldNotMatch(@"\. [a-z]");
            }
            finally
            {
                trace.End();
            }
        }
        finally
        {
            release.Set();
            other.Join(TimeSpan.FromMinutes(1)).ShouldBeTrue();
        }
    }

    [Fact]
    public void Cancellation_BeforeAnyNativeCall_SaysSo_AndReportsTheWaitToStart()
    {
        NativeCallTrace trace = NativeCallTrace.Begin(TimeSpan.FromSeconds(42));
        try
        {
            string message = trace.DescribeCancellation();

            message.ShouldContain("having waited 42 s for a thread before it started", Case.Sensitive);
            message.ShouldContain("cancelled before its first native compiler call", Case.Sensitive);
        }
        finally
        {
            trace.End();
        }
    }

    [Fact]
    public void Traces_Nest_AndEndRestoresTheOuterOne()
    {
        NativeCallTrace outer = NativeCallTrace.Begin(TimeSpan.Zero);
        try
        {
            NativeCallTrace inner = NativeCallTrace.Begin(TimeSpan.Zero);
            NativeCallTrace.Current.ShouldBeSameAs(inner);
            inner.End();
            NativeCallTrace.Current.ShouldBeSameAs(outer);
        }
        finally
        {
            outer.End();
        }

        NativeCallTrace.Current.ShouldBeNull();
    }

    [Fact]
    public void TheUnnamedOverload_RecordsNothing()
    {
        NativeCallTrace trace = NativeCallTrace.Begin(TimeSpan.Zero);
        try
        {
            NativeCompileStack.Run(() => 3).ShouldBe(3);

            trace.DescribeCancellation().ShouldContain("before its first native compiler call", Case.Sensitive);
        }
        finally
        {
            trace.End();
        }
    }
}
