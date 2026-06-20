using System.Runtime.CompilerServices;

namespace ShadowDusk.ShaderToy.Tests;

/// <summary>
/// Locates the SOURCE <c>corpus</c> directory in the repo tree (next to this test file), not the
/// copied-to-output copy. Anchoring to <see cref="CallerFilePathAttribute"/> means generated goldens
/// (the <c>SHADERTOY2FX_UPDATE_GOLDENS=1</c> path) land in the committed source tree rather than the
/// throwaway <c>bin/</c> output. Reading inputs from source also keeps theories stable regardless of
/// copy-to-output timing.
/// </summary>
internal static class CorpusLocator
{
    /// <summary>Absolute path to the source <c>corpus</c> directory (sibling of this file).</summary>
    public static string CorpusDir { get; } = ComputeCorpusDir();

    /// <summary>Environment-variable opt-in: when "1", goldens are (re)written instead of asserted.</summary>
    public static bool UpdateGoldens =>
        Environment.GetEnvironmentVariable("SHADERTOY2FX_UPDATE_GOLDENS") == "1";

    public static string AuthoredDir => Path.Combine(CorpusDir, "authored");

    public static string RejectDir => Path.Combine(CorpusDir, "reject");

    public static string Cc0Dir => Path.Combine(CorpusDir, "cc0");

    public static string GoldenDir => Path.Combine(CorpusDir, "golden");

    /// <summary>All <c>*.glsl</c> files under <paramref name="dir"/>, sorted for deterministic order.</summary>
    public static IEnumerable<string> GlslFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(dir, "*.glsl", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);
    }

    /// <summary>Normalize line endings to <c>\n</c> so goldens compare identically across OSes.</summary>
    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string ComputeCorpusDir([CallerFilePath] string thisFilePath = "")
    {
        string? dir = Path.GetDirectoryName(thisFilePath);
        if (dir is null)
        {
            throw new InvalidOperationException("Could not resolve the test file directory.");
        }

        string corpus = Path.Combine(dir, "corpus");
        if (!Directory.Exists(corpus))
        {
            throw new DirectoryNotFoundException(
                $"Source corpus directory not found at '{corpus}'. Expected it next to the test sources.");
        }

        return corpus;
    }
}
