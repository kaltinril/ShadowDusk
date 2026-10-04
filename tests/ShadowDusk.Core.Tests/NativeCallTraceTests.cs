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
