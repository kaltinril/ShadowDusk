#nullable enable

namespace ShadowDusk.Core;

public enum ShaderErrorKind { Compile, IncludeNotFound, CircularInclude }

public sealed record ShaderError(
    string File,
    int Line,
    int Column,
    string Code,
    string Message,
    ShaderErrorSeverity Severity = ShaderErrorSeverity.Error,
    ShaderErrorKind Kind = ShaderErrorKind.Compile,
    string? IncludingFilePath = null,
    int IncludingLineNumber = 0,
    string? RequestedPath = null,
    IReadOnlyList<string>? SearchedPaths = null,
    string? RawDiagnostics = null
)
{
    public static ShaderError IncludeNotFound(
        string includingFile,
        int line,
        string requested,
        IReadOnlyList<string> searched)
        => new(
            includingFile,
            line,
            0,
            "SD0001",
            $"Cannot find include '{requested}'",
            Kind: ShaderErrorKind.IncludeNotFound,
            IncludingFilePath: includingFile,
            IncludingLineNumber: line,
            RequestedPath: requested,
            SearchedPaths: searched);

    public static ShaderError CircularInclude(
        string includingFile,
        int line,
        string requested)
        => new(
            includingFile,
            line,
            0,
            "SD0002",
            $"Circular include detected: '{requested}'",
            Kind: ShaderErrorKind.CircularInclude,
            IncludingFilePath: includingFile,
            IncludingLineNumber: line,
            RequestedPath: requested);

    public string FxcFormattedMessage =>
        $"{File}({Line},{Column}-{Column}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

public enum ShaderErrorSeverity { Warning, Error, Note }
