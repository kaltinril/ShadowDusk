#nullable enable

using System.Collections.Concurrent;

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// The disk-backed <see cref="IIncludePathCanonicalizer"/>: walks a path segment by segment and
/// asks the file system for each segment's real name.
/// </summary>
/// <remarks>
/// <para>Read-only by construction — it enumerates, it never creates a probe file, so it works
/// on a read-only volume and leaves nothing behind.</para>
/// <para>Each segment is looked up with <see cref="MatchCasing.CaseInsensitive"/> so a
/// wrong-cased request still finds the real entry, and an <b>exact ordinal match always wins</b>
/// over a case-insensitive one. That second rule is what makes this correct on a case-sensitive
/// volume that genuinely holds both <c>Common.fxh</c> and <c>common.fxh</c>: each spelling
/// canonicalizes to itself instead of collapsing onto whichever the directory listed first.</para>
/// <para>Results are cached per full path for the process lifetime. Include trees do not change
/// during a compile, and the cache keeps a deep include chain from re-enumerating its shared
/// prefix once per file.</para>
/// </remarks>
public sealed class FileSystemIncludePathCanonicalizer : IIncludePathCanonicalizer
{
    /// <summary>The shared instance used by <see cref="Preprocessor"/> when none is injected.</summary>
    public static FileSystemIncludePathCanonicalizer Instance { get; } = new();

    // A pattern containing a wildcard would match the WRONG entry, so those segments decline
    // rather than guess. Real header names do not contain them.
    private static readonly char[] WildcardChars = ['*', '?'];

    private static readonly EnumerationOptions LookupOptions = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        MatchType = MatchType.Simple,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        // The default skips Hidden and System; a dot-prefixed shader directory is Hidden on
        // Unix semantics, and skipping it would report a real file as un-canonicalizable.
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
    };

    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string? TryGetOnDiskPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        return _cache.GetOrAdd(path, static p => Canonicalize(p));
    }

    private static string? Canonicalize(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                      or PathTooLongException or IOException
                                      or UnauthorizedAccessException)
        {
            return null;
        }

        // Only an existing path has an on-disk spelling to report. A virtual/in-memory name
        // lands here and correctly answers "unknown".
        if (!File.Exists(full) && !Directory.Exists(full))
            return null;

        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length == 0)
            return null;

        string[] segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        string current = root;
        foreach (string segment in segments)
        {
            string? real = RealName(current, segment);
            if (real is null)
                return null;

            current = Path.Combine(current, real);
        }

        return current;
    }

    /// <summary>
    /// Returns the name <paramref name="parent"/> really holds for <paramref name="segment"/>,
    /// preferring an exact ordinal match over a case-insensitive one.
    /// </summary>
    private static string? RealName(string parent, string segment)
    {
        if (segment.IndexOfAny(WildcardChars) >= 0)
            return null;

        try
        {
            string? caseInsensitiveMatch = null;

            foreach (string entry in Directory.EnumerateFileSystemEntries(parent, segment, LookupOptions))
            {
                string name = Path.GetFileName(entry);

                if (string.Equals(name, segment, StringComparison.Ordinal))
                    return name;

                if (caseInsensitiveMatch is null &&
                    string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveMatch = name;
                }
            }

            return caseInsensitiveMatch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
