#nullable enable

namespace ShadowDusk.ImageTests.GlContext;

/// <summary>
/// Pure decision logic for the <c>SHADOWDUSK_REQUIRE_GL</c> environment gate
/// (Phase 37 verification-tail item 4 — soft-skip hardening).
///
/// <para>
/// <b>Why this exists:</b> Phase 37 Finding B's "paradox" — every ImageTests
/// class early-returns <i>as PASS</i> when <see cref="GlContextFixture.IsSkipped"/>
/// (headless runner, no GL context). On headless CI that fabricated
/// "27/27 PASS" runs that never compiled a single shader, masking a total
/// Linux compile failure for weeks. With <c>SHADOWDUSK_REQUIRE_GL</c> set
/// (any value except empty/<c>0</c>/<c>false</c>), a missing GL context is a
/// LOUD FAILURE instead of a silent pass — CI lanes that are supposed to
/// render (ubuntu under xvfb + Mesa llvmpipe) set it so a regression back to
/// headless-skip turns the lane red.
/// </para>
///
/// <para>
/// Kept free of environment reads and GL calls so the gate semantics are
/// unit-testable (<c>GlRequirementTests</c>).
/// </para>
/// </summary>
public static class GlRequirement
{
    /// <summary>Name of the gate environment variable.</summary>
    public const string EnvVar = "SHADOWDUSK_REQUIRE_GL";

    /// <summary>
    /// Interprets the raw environment-variable value. Unset / empty /
    /// whitespace / <c>0</c> / <c>false</c> (case-insensitive) mean "not
    /// required" (visible soft-skip allowed); anything else — including the
    /// canonical <c>1</c> — means "required" (no GL context = hard failure).
    /// </summary>
    public static bool IsRequired(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue))
            return false;

        string v = envValue.Trim();
        return v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The failure message thrown when GL is required but unavailable.
    /// Includes the underlying context-creation failure and how to resolve
    /// (provide software GL, or unset the gate).
    /// </summary>
    public static string BuildFailureMessage(string? skipReason) =>
        $"{EnvVar} is set but no OpenGL 3.3 Compatibility context could be created — " +
        "failing LOUDLY instead of soft-skipping (Phase 37 Finding B: soft-skip-as-pass once " +
        "masked a total Linux compile failure). " +
        $"Underlying reason: {skipReason ?? "unknown"}. " +
        "Either provide a GL-capable environment (headless CI: xvfb-run -a + Mesa llvmpipe with " +
        $"LIBGL_ALWAYS_SOFTWARE=1, see ci.yml's ubuntu lane) or unset {EnvVar} to allow the " +
        "visible soft-skip.";

    /// <summary>
    /// The unmistakable log line emitted once per test class when the suite
    /// soft-skips (gate unset). Makes "passed without rendering" readable in
    /// any log — a green ImageTests summary with this line means rendered 0.
    /// </summary>
    public static string BuildSoftSkipNotice(string? skipReason) =>
        "[ShadowDusk.ImageTests] GL SOFT-SKIP (rendered 0): every test in this class will PASS " +
        $"WITHOUT RENDERING. Reason: {skipReason ?? "unknown"}. " +
        $"Set {EnvVar}=1 to make a missing GL context a hard failure instead.";
}
