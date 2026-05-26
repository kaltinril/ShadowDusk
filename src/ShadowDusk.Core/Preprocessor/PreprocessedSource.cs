#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public sealed record PreprocessedSource(
    string Text,
    IReadOnlyList<string> DxcMacroFlags,
    string OriginalFilePath);
