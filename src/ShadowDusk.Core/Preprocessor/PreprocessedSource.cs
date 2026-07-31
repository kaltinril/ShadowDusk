#nullable enable

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// The output of the include-flattening preprocessor pass: the fully expanded source text,
/// the DXC macro flags to pass downstream, and the original file path for diagnostics.
/// </summary>
/// <param name="Text">The flattened source with all <c>#include</c> directives expanded.</param>
/// <param name="DxcMacroFlags">The platform macro flags to forward to DXC.</param>
/// <param name="OriginalFilePath">The original source file path, preserved for diagnostics.</param>
/// <param name="Warnings">
/// Non-fatal diagnostics raised while flattening — currently the <c>SD0008</c> case-only
/// <c>#include</c> mismatch. The pipeline forwards these onto
/// <see cref="CompiledShader.Warnings"/>; they never gate output.
/// </param>
public sealed record PreprocessedSource(
    string Text,
    IReadOnlyList<string> DxcMacroFlags,
    string OriginalFilePath,
    IReadOnlyList<ShaderError> Warnings)
{
    /// <summary>Creates a warning-free preprocessed source.</summary>
    /// <param name="text">The flattened source.</param>
    /// <param name="dxcMacroFlags">The platform macro flags to forward to DXC.</param>
    /// <param name="originalFilePath">The original source file path.</param>
    public PreprocessedSource(
        string text,
        IReadOnlyList<string> dxcMacroFlags,
        string originalFilePath)
        : this(text, dxcMacroFlags, originalFilePath, [])
    {
    }
}
