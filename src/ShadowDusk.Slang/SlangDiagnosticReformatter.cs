#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Slang;

/// <summary>
/// Parses slangc's own diagnostic text (a Rust-compiler-style, multi-line format — a
/// <c>error[E#####]: summary</c> header line, a <c>--&gt; file:line:col</c> location line,
/// then a source code frame) into <see cref="ShaderError"/>, matching
/// <c>ShadowDusk.HLSL.Dxc.DxcDiagnosticReformatter</c>'s shape for the DXC diagnostic
/// format: locate what can be located, but never reword the compiler's own text — the
/// complete block becomes <see cref="ShaderError.Message"/> verbatim (there is no compact
/// one-line form to extract from Slang's diagnostics the way there is from DXC's), and the
/// complete original stderr text always rides on <see cref="ShaderError.RawDiagnostics"/>.
/// </summary>
internal static partial class SlangDiagnosticReformatter
{
    [GeneratedRegex(
        @"^(?<severity>error|warning)\[(?<code>[A-Za-z0-9]+)\]:.*$",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex DiagnosticHeader();

    [GeneratedRegex(
        @"^\s*-->\s*(?<file>.+):(?<line>\d+):(?<col>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex LocationLine();

    /// <summary>
    /// Splits slangc's stderr text into one <see cref="ShaderError"/> per
    /// <c>error[...]</c>/<c>warning[...]</c> block. <paramref name="sourceFileName"/>
    /// always wins for <see cref="ShaderError.File"/>: slangc echoes back whatever input
    /// name it was given (a temp path, or <c>&lt;stdin&gt;</c> when the source is piped in,
    /// as this project's <c>SlangCompiler</c> does), which is never the caller's logical
    /// source name and there is exactly one input file per compile, so there is no
    /// ambiguity to preserve by keeping slangc's own path.
    /// </summary>
    public static IReadOnlyList<ShaderError> Reformat(string slangcStderr, string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(slangcStderr))
            return [];

        MatchCollection headers = DiagnosticHeader().Matches(slangcStderr);
        if (headers.Count == 0)
        {
            // Nothing matched the expected shape (an unanticipated slangc failure mode —
            // a native crash, an internal-compiler-error, a CLI usage error). Never invent
            // a message: hand back the complete text as a single located-at-nothing error,
            // the same fallback DxcDiagnosticReformatter uses for its own unparsed remainder.
            string verbatim = slangcStderr.TrimEnd();
            return
            [
                new ShaderError(
                    File: sourceFileName, Line: 0, Column: 0, Code: "SD0622",
                    Message: verbatim, Severity: ShaderErrorSeverity.Error, RawDiagnostics: verbatim),
            ];
        }

        string fullText = slangcStderr.TrimEnd();
        var errors = new List<ShaderError>(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            int start = headers[i].Index;
            int end = i + 1 < headers.Count ? headers[i + 1].Index : slangcStderr.Length;
            string block = slangcStderr[start..end].TrimEnd();

            string code = headers[i].Groups["code"].Value;
            ShaderErrorSeverity severity = headers[i].Groups["severity"].Value == "warning"
                ? ShaderErrorSeverity.Warning
                : ShaderErrorSeverity.Error;

            int line = 0, col = 0;
            Match loc = LocationLine().Match(block);
            if (loc.Success)
            {
                line = int.Parse(loc.Groups["line"].Value);
                col = int.Parse(loc.Groups["col"].Value);
            }

            errors.Add(new ShaderError(
                File: sourceFileName,
                Line: line,
                Column: col,
                Code: code,
                Message: block,
                Severity: severity,
                RawDiagnostics: fullText));
        }

        return errors;
    }

    /// <summary>
    /// Selects the single primary <see cref="ShaderError"/> for a failed slangc invocation:
    /// the first error-severity diagnostic, else the first diagnostic of any severity, else
    /// (slangc exited non-zero with no parseable — or no — stderr at all) a synthesized
    /// <c>SD0622</c> naming the entry point and stage that failed.
    /// </summary>
    public static ShaderError SelectPrimary(
        string slangcStderr, string sourceFileName, string entryName, string stageLabel)
    {
        IReadOnlyList<ShaderError> errors = Reformat(slangcStderr, sourceFileName);

        foreach (ShaderError e in errors)
        {
            if (e.Severity == ShaderErrorSeverity.Error)
                return e;
        }
        if (errors.Count > 0)
            return errors[0];

        string raw = string.IsNullOrWhiteSpace(slangcStderr) ? "" : slangcStderr.TrimEnd();
        return new ShaderError(
            File: sourceFileName,
            Line: 0,
            Column: 0,
            Code: "SD0622",
            Message: raw.Length > 0
                ? raw
                : $"slangc failed compiling entry point '{entryName}' ({stageLabel}) with no diagnostic output.",
            Severity: ShaderErrorSeverity.Error,
            RawDiagnostics: raw.Length > 0 ? raw : null);
    }
}
