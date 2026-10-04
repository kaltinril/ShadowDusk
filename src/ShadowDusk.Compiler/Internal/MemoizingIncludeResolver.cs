#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// One compile's view of the consumer's <see cref="IIncludeResolver"/>: each distinct
/// (include path, including file, search paths) is resolved ONCE and the answer reused.
///
/// <para>A compile flattens the source more than once: the compile itself, the preprocessed
/// views the OpenGL sampler allocator and the overlapping-register check (<c>SD0227</c>) read,
/// and the legacy-sampler recovery (issue #308). Each used to call the consumer's resolver
/// again, so a resolver that counts, logs, reads a changing store, or throws on a repeat call
/// saw a different compile than its first answer described. Memoizing makes every pass see the
/// first answer, failures included. An exception from the consumer's resolver is not caught: it
/// surfaces on the first call, exactly as before.</para>
/// </summary>
internal sealed class MemoizingIncludeResolver : IIncludeResolver
{
    private readonly IIncludeResolver _inner;
    private readonly Dictionary<string, Result<IncludeResolvedFile, ShaderError>> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private MemoizingIncludeResolver(IIncludeResolver inner) => _inner = inner;

    /// <summary>Wraps <paramref name="inner"/>, unless it is already a memoizing wrapper.</summary>
    public static IIncludeResolver Wrap(IIncludeResolver inner) =>
        inner as MemoizingIncludeResolver ?? new MemoizingIncludeResolver(inner);

    public Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath, string? includingFilePath, IReadOnlyList<string> additionalSearchPaths)
    {
        string key = string.Join("\u0000", new[] { includePath, includingFilePath ?? "" }.Concat(additionalSearchPaths));
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out Result<IncludeResolvedFile, ShaderError> cached))
                return cached;
        }

        Result<IncludeResolvedFile, ShaderError> result = _inner.Resolve(includePath, includingFilePath, additionalSearchPaths);
        lock (_gate)
        {
            _cache.TryAdd(key, result);
            return _cache[key];
        }
    }
}
