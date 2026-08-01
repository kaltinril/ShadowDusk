#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// Reports the spelling a resolved <c>#include</c> path <i>actually has</i> on the storage it
/// came from, so the preprocessor can decide whether two differently-cased paths name the same
/// file by <b>asking</b> rather than by guessing from the operating system.
/// </summary>
/// <remarks>
/// <para>The preprocessor keys its cycle-detection stack and its <c>#pragma once</c> set on
/// resolved paths, so it needs to know when <c>Shared/Common.fxh</c> and
/// <c>shared/common.fxh</c> are one file and when they are two. Inferring that from the host OS
/// is wrong in both directions: <b>Android's file system is case-sensitive</b> (it is Linux),
/// and <b>APFS can be formatted case-sensitive</b>, so "not Linux ⇒ case-insensitive" merges
/// two genuinely distinct headers; conversely a case-insensitive volume mounted on Linux, or a
/// per-directory case-sensitive NTFS directory on Windows, breaks the opposite assumption.</para>
/// <para>Canonicalizing sidesteps the question entirely: on a case-insensitive volume both
/// spellings canonicalize to the one name on disk (so they compare equal), and on a
/// case-sensitive volume each spelling canonicalizes to itself (so they compare distinct). No
/// OS check is involved, and the answer is right on a host nobody has tested on.</para>
/// <para>This is an interface so a unit test can drive both file-system behaviours without a
/// disk, and so an <see cref="IIncludeResolver"/> serving a virtual file set can describe its
/// own naming rules.</para>
/// </remarks>
public interface IIncludePathCanonicalizer
{
    /// <summary>
    /// Returns <paramref name="path"/> re-spelled exactly as the storage holding it spells it,
    /// or <see langword="null"/> when that cannot be determined (the path does not exist, it is
    /// a virtual name with no backing store, or the lookup failed).
    /// </summary>
    /// <param name="path">A resolved path, as produced by an <see cref="IIncludeResolver"/>.</param>
    /// <returns>
    /// The on-disk spelling, or <see langword="null"/> if unknown. A <see langword="null"/>
    /// answer must never be read as "the same as some other path": callers treat unknown as
    /// <i>case-sensitive</i> (ordinal), the conservative choice, because that never merges two
    /// paths that might be different files.
    /// </returns>
    string? TryGetOnDiskPath(string path);
}
