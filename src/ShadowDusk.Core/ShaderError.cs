#nullable enable

namespace ShadowDusk.Core;

/// <summary>Categorizes the kind of failure a <see cref="ShaderError"/> represents.</summary>
public enum ShaderErrorKind
{
    /// <summary>A general HLSL compilation or transpilation error.</summary>
    Compile,
    /// <summary>An <c>#include</c> file could not be found on any search path.</summary>
    IncludeNotFound,
    /// <summary>An <c>#include</c> cycle was detected.</summary>
    CircularInclude,
}

/// <summary>
/// A single diagnostic produced during compilation, carrying the source location and the
/// underlying compiler's message verbatim. Errors are returned (never swallowed or
/// reformatted) so callers can surface the exact file, line, column, and message.
/// </summary>
/// <param name="File">The source file the diagnostic refers to.</param>
/// <param name="Line">The 1-based line number, or 0 when not applicable.</param>
/// <param name="Column">The 1-based column number, or 0 when not applicable.</param>
/// <param name="Code">The diagnostic code (e.g. <c>SD0001</c> or an underlying compiler code).</param>
/// <param name="Message">The human-readable diagnostic message.</param>
/// <param name="Severity">The severity; defaults to <see cref="ShaderErrorSeverity.Error"/>.</param>
/// <param name="Kind">The kind of failure; defaults to <see cref="ShaderErrorKind.Compile"/>.</param>
/// <param name="IncludingFilePath">For include errors, the file that issued the <c>#include</c>.</param>
/// <param name="IncludingLineNumber">For include errors, the line of the <c>#include</c> directive.</param>
/// <param name="RequestedPath">For include errors, the path that was requested.</param>
/// <param name="SearchedPaths">For <see cref="ShaderErrorKind.IncludeNotFound"/>, the directories searched.</param>
/// <param name="RawDiagnostics">The raw, unparsed diagnostic text from the underlying compiler, when available.</param>
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
    /// <summary>
    /// Creates an <see cref="ShaderErrorKind.IncludeNotFound"/> error (code <c>SD0001</c>) for
    /// an <c>#include</c> that could not be resolved on any search path.
    /// </summary>
    /// <param name="includingFile">The file that issued the <c>#include</c>.</param>
    /// <param name="line">The line of the <c>#include</c> directive.</param>
    /// <param name="requested">The include path that was requested.</param>
    /// <param name="searched">The directories that were searched.</param>
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

    /// <summary>
    /// Creates a <see cref="ShaderErrorKind.CircularInclude"/> error (code <c>SD0002</c>) for a
    /// detected <c>#include</c> cycle.
    /// </summary>
    /// <param name="includingFile">The file that issued the cyclic <c>#include</c>.</param>
    /// <param name="line">The line of the <c>#include</c> directive.</param>
    /// <param name="requested">The include path that re-entered the cycle.</param>
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

    /// <summary>
    /// The diagnostic rendered in the <c>fxc</c>/<c>mgfxc</c> message format
    /// (<c>file(line,col-col): severity code: message</c>), so MGCB and IDEs can parse it.
    /// </summary>
    public string FxcFormattedMessage =>
        $"{File}({Line},{Column}-{Column}): {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

/// <summary>The severity level of a <see cref="ShaderError"/>.</summary>
public enum ShaderErrorSeverity
{
    /// <summary>A non-fatal warning.</summary>
    Warning,
    /// <summary>A fatal error that fails compilation.</summary>
    Error,
    /// <summary>An informational note.</summary>
    Note,
}
