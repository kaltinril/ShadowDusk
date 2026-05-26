#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public sealed record IncludeResolvedFile(string FilePath, string Text);
