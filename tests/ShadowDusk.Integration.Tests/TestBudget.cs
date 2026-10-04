#nullable enable

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// The wall-clock budget a test gives one compile, or a handful of compiles, in process or
/// through a child CLI (issues #373, #316). One number, here, so it is set from measurement
/// once instead of guessed per file.
/// </summary>
/// <remarks>
/// <para>
/// A budget is not a hang guard: a token cannot interrupt a native compiler call, and the
/// integration step's <c>--blame-hang-timeout</c> is what kills and dumps a stuck host. A budget
/// only fails one test that is far slower than it should be while the rest of the assembly
/// keeps running (which the blame timer never notices), so it must sit above anything a
/// healthy compile does on a loaded CI runner, and there is no value in it being shorter than
/// the blame timeout.
/// </para>
/// <para>
/// Measured (2026-10-03, every integration run on CI from 2026-10-02 to 2026-10-03): on
/// windows-latest, with four test hosts sharing the 4-vCPU runner, one host at a time made no
/// progress at all for up to 131 s in the middle of a run (up to 176 s at start-up), so 30 and
/// 60 s budgets around a sub-second compile failed. The integration step now runs at most two
/// test hosts at once; over 4 CI runs per lane the longest no-progress gap was then 28 s and the
/// slowest single test 67 s. This budget is the blame timeout (3 min): 2.7 times the slowest
/// test since that change, and 1.37 times the worst stall ever measured before it.
/// </para>
/// </remarks>
internal static class TestBudget
{
    /// <summary>Budget for one compile or a few: the CI blame-hang timeout, 3 minutes.</summary>
    public static readonly TimeSpan Compile = TimeSpan.FromMinutes(3);
}
