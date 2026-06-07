#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>The successful result of resolving an <c>#include</c>: the file's path and text.</summary>
/// <param name="FilePath">The resolved file path.</param>
/// <param name="Text">The file's source text.</param>
public sealed record IncludeResolvedFile(string FilePath, string Text);
