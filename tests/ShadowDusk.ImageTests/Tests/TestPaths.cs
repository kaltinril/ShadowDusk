#nullable enable

namespace ShadowDusk.ImageTests.Tests;

/// <summary>
/// Helpers for resolving paths that point at the source tree (NOT the test
/// bin/ output). Used by both the bootstrap (which writes PNGs into git) and
/// the regression tests (which read them).
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> looking for the
    /// repo root, identified by the presence of <c>ShadowDusk.slnx</c>. Can
    /// be overridden by setting <c>SHADOWDUSK_REPO_ROOT</c>.
    /// </summary>
    public static string FindRepoRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable("SHADOWDUSK_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && File.Exists(Path.Combine(overrideRoot, "ShadowDusk.slnx")))
            return overrideRoot;

        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "ShadowDusk.slnx")))
                return dir;

            string? parent = Path.GetDirectoryName(dir);
            if (parent == dir || parent is null)
                break;
            dir = parent;
        }

        throw new InvalidOperationException(
            $"Could not locate ShadowDusk.slnx walking up from '{AppContext.BaseDirectory}'. " +
            "Set SHADOWDUSK_REPO_ROOT to override.");
    }
}
