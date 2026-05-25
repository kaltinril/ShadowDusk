#nullable enable

namespace ShadowDusk.Core;

public sealed record ShaderError(
    string File,
    int    Line,
    int    Column,
    string Code,
    string Message,
    ShaderErrorSeverity Severity = ShaderErrorSeverity.Error
);

public enum ShaderErrorSeverity { Warning, Error }
