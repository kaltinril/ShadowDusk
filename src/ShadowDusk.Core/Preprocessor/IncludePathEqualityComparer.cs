#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Compares resolved include paths the way the storage they came from does: ordinal by default,
/// with two case-only variants treated as one file <b>only</b> when the
/// <see cref="IIncludePathCanonicalizer"/> confirms they canonicalize to the same name.
/// </summary>
/// <remarks>
/// <para>Ordinal is the default and case-insensitivity is the exception, not the other way
/// round. Merging two paths that are actually different files is the damaging error (a
/// <c>#pragma once</c> in one suppresses the other, or a legal include chain is rejected as a
/// cycle); failing to merge two spellings of one file only costs a duplicated expansion that
/// the existing cycle check still terminates.</para>
/// <para><see cref="GetHashCode(string)"/> hashes case-insensitively so both spellings land in
/// the same bucket and <see cref="Equals(string,string)"/> gets to make the decision. Over-wide
/// hashing is safe; a narrower hash would make the equality rule unreachable.</para>
/// </remarks>
internal sealed class IncludePathEqualityComparer : IEqualityComparer<string>
{
    private readonly IIncludePathCanonicalizer _canonicalizer;

    public IncludePathEqualityComparer(IIncludePathCanonicalizer canonicalizer)
        => _canonicalizer = canonicalizer;

    public bool Equals(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (string.Equals(x, y, StringComparison.Ordinal))
            return true;

        // Anything that is not a pure case difference is a different path on every file system,
        // so the canonicalizer is never consulted for it.
        if (!string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
            return false;

        string? canonicalX = _canonicalizer.TryGetOnDiskPath(x);
        if (canonicalX is null)
            return false;

        string? canonicalY = _canonicalizer.TryGetOnDiskPath(y);
        if (canonicalY is null)
            return false;

        return string.Equals(canonicalX, canonicalY, StringComparison.Ordinal);
    }

    public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
}
