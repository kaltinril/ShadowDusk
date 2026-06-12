#nullable enable

namespace ShadowDusk.Tests.Shared;

/// <summary>
/// Pure decision logic for the <c>SHADOWDUSK_REQUIRE_VKD3D</c> / <c>SHADOWDUSK_REQUIRE_DXC</c>
/// environment gates — the attribute-level analog of ImageTests'
/// <c>SHADOWDUSK_REQUIRE_GL</c> (<c>GlRequirement</c>).
///
/// <para>
/// <b>Why this exists:</b> the vkd3d / macOS-DXC availability gates
/// (<c>FnaTestGate</c>, <c>Vkd3dTestGate</c>, <c>DxcTestGate</c>) skip-with-reason when
/// the restored native is absent. That is the right LOCAL behavior, but in CI — where
/// the pinned natives are hosted and <c>tools/restore.*</c> provisions them on every
/// run (Phase 37 A/C) — a missing native means the restore infrastructure failed, and
/// the suite must go RED, never quietly skip green (the repo's documented recurring
/// failure mode; see Phase 37 Finding B for the GL flavor of the same trap). CI lanes
/// that restore the natives set these variables so missing-native flips skip → FAIL.
/// </para>
///
/// <para>
/// Mechanism note: an xUnit <c>FactAttribute</c> cannot itself "fail" a test — it can
/// only skip or let the test run. When a gate is required and the native is absent, the
/// attributes therefore do NOT set <c>Skip</c>: the test executes and fails loudly at
/// the native boundary (SD0211 for vkd3d, the DXC load failure on macOS) — a genuine
/// red, with the underlying loader diagnostic in the failure output.
/// </para>
///
/// <para>
/// Kept free of environment reads and native probes so the gate semantics are
/// unit-testable (<c>NativeRequirementTests</c>) on every host — the
/// <c>GlRequirement</c> pattern.
/// </para>
/// </summary>
public static class NativeRequirement
{
    /// <summary>Gate variable: a missing vkd3d-shader native is a hard FAILURE, not a skip.</summary>
    public const string Vkd3dEnvVar = "SHADOWDUSK_REQUIRE_VKD3D";

    /// <summary>Gate variable: a missing DXC native (the restored macOS dylib) is a hard FAILURE, not a skip.</summary>
    public const string DxcEnvVar = "SHADOWDUSK_REQUIRE_DXC";

    /// <summary>
    /// Interprets the raw environment-variable value — identical semantics to
    /// <c>GlRequirement.IsRequired</c>: unset / empty / whitespace / <c>0</c> /
    /// <c>false</c> (case-insensitive) mean "not required" (skip-with-reason allowed);
    /// anything else — including the canonical <c>1</c> — means "required"
    /// (missing native = the test runs and fails loudly instead of skipping).
    /// </summary>
    public static bool IsRequired(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue))
            return false;

        string v = envValue.Trim();
        return v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the test should SKIP: the native is unavailable AND the gate is not
    /// required. When the gate IS required, an unavailable native must not skip — the
    /// test runs and fails at the native boundary.
    /// </summary>
    public static bool ShouldSkip(bool nativeAvailable, string? envValue) =>
        !nativeAvailable && !IsRequired(envValue);
}
