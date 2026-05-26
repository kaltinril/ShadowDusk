#nullable enable

using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.HLSL;

/// <summary>A structured diagnostic error produced by the FX9 pre-parser.</summary>
public sealed record FxParseError
{
    /// <summary>The source file path or display name passed to the parser.</summary>
    public required string SourceFile { get; init; }

    /// <summary>1-based line number of the error location.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column number of the error location.</summary>
    public required int Column { get; init; }

    /// <summary>Human-readable error description.</summary>
    public required string Message { get; init; }

    /// <summary>Diagnostic code identifying the error category.</summary>
    public required FxParseErrorCode Code { get; init; }

    /// <summary>Source span covering the token or construct that caused the error.</summary>
    public required SourceSpan Span { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{SourceFile}({Line},{Column}): error FX{(int)Code:D4}: {Message}";
}
