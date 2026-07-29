using System;
using System.IO;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// Finds the authored ShaderToy corpus.
/// </summary>
/// <remarks>
/// Phase 47 promoted the converter out of <c>tools/shadertoy2fx/</c> into the in-solution
/// <c>src/ShadowDusk.ShaderToy/</c> + <c>tests/ShadowDusk.ShaderToy.Tests/</c>, which moved the
/// corpus with it. This out-of-band driver kept pointing at the pre-promotion location and had
/// been dead ever since (it exits 2 with "authored corpus not found" before rendering anything) -
/// it is not in <c>ShadowDusk.slnx</c>, so nothing caught it. Found 2026-07-28.
///
/// Probes the current location first, then the legacy one, so an old checkout still works.
/// </remarks>
internal static class CorpusLocator
{
    /// <summary>Candidate corpus roots, most-current first.</summary>
    private static readonly string[][] Candidates =
    {
        // Current: promoted in-solution by Phase 47.
        new[] { "tests", "ShadowDusk.ShaderToy.Tests", "corpus", "authored" },
        // Legacy: pre-Phase-47, when the converter lived entirely under tools/.
        new[] { "tools", "shadertoy2fx", "tests", "ShadowDusk.ShaderToy.Tests", "corpus", "authored" },
    };

    /// <summary>
    /// Returns the authored-corpus directory. If none exists, returns the current-location path
    /// so the caller's error message names where it should be.
    /// </summary>
    public static string FindAuthored(string repoRoot)
    {
        string? first = null;
        foreach (string[] parts in Candidates)
        {
            string candidate = Path.Combine(repoRoot, Path.Combine(parts));
            first ??= candidate;
            if (Directory.Exists(candidate))
                return candidate;
        }

        return first!;
    }
}
