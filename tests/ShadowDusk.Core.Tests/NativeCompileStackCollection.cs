#nullable enable

using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// The tests that drive <see cref="ShadowDusk.Core.NativeCompileStack"/> directly share its
/// process-wide worker pool, so they must not run beside each other or beside any other test.
/// <c>Run_SequentialCalls_ReuseOneWorker</c> asserts that back-to-back calls reuse one worker;
/// a concurrent caller in another class (<c>NativeCallTraceTests</c>) can take that worker
/// between two calls and force a second one, which failed CI on ubuntu net10.0.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeCompileStackCollection
{
    public const string Name = "NativeCompileStack (no parallel)";
}
