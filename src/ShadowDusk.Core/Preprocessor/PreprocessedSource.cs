#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// The output of the include-flattening preprocessor pass: the fully expanded source text,
/// the DXC macro flags to pass downstream, and the original file path for diagnostics.
/// </summary>
/// <param name="Text">The flattened source with all <c>#include</c> directives expanded.</param>
/// <param name="DxcMacroFlags">The platform macro flags to forward to DXC.</param>
/// <param name="OriginalFilePath">The original source file path, preserved for diagnostics.</param>
public sealed record PreprocessedSource(
    string Text,
    IReadOnlyList<string> DxcMacroFlags,
    string OriginalFilePath);
