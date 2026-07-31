#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.MgcbPlugin;

/// <summary>
/// Wraps another <see cref="IIncludeResolver"/> and records every <c>#include</c> file it
/// successfully resolved, so the processor can register them with
/// <c>ContentProcessorContext.AddDependency</c>. Without that, MGCB's incremental build
/// only watches the <c>.fx</c> itself and an edit to an <c>.fxh</c> silently does not rebuild.
/// <para>
/// A decorator, not a second resolver: resolution behavior is entirely the inner resolver's,
/// so include semantics stay identical to the CLI's.
/// </para>
/// </summary>
internal sealed class RecordingIncludeResolver : IIncludeResolver
{
    private readonly IIncludeResolver _inner;
    private readonly List<string> _resolved = [];

    public RecordingIncludeResolver(IIncludeResolver inner) => _inner = inner;

    /// <summary>The distinct full paths of every include successfully resolved, in first-seen order.</summary>
    public IReadOnlyList<string> ResolvedPaths => _resolved;

    /// <inheritdoc/>
    public Result<IncludeResolvedFile, ShaderError> Resolve(
        string includePath,
        string? includingFilePath,
        IReadOnlyList<string> additionalSearchPaths)
    {
        var result = _inner.Resolve(includePath, includingFilePath, additionalSearchPaths);

        if (result.IsSuccess &&
            result.Value.FilePath.Length > 0 &&
            !_resolved.Contains(result.Value.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            _resolved.Add(result.Value.FilePath);
        }

        return result;
    }
}
