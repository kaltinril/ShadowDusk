#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.D3DCompiler;

/// <summary>
/// Reformats d3dcompiler_47 (fxc) error-blob text into <see cref="ShaderError"/>s.
/// fxc emits MSVC-style diagnostics: <c>&lt;file&gt;(&lt;line&gt;,&lt;col&gt;): error X0000: &lt;message&gt;</c>
/// (the column may be a range, e.g. <c>(12,5-9)</c>). The file part can contain a
/// drive letter, so the line/col group is anchored to the parenthesised suffix.
/// Constraint 5: surface file/line/column/message verbatim — no swallowing.
/// </summary>
internal static partial class D3DCompilerDiagnosticReformatter
{
    [GeneratedRegex(
        @"^(?<file>.+)\((?<line>\d+)(?:,(?<col>\d+)(?:-\d+)?)?\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z0-9]+)\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();

    public static IReadOnlyList<ShaderError> Reformat(string fxcErrorText, string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(fxcErrorText))
            return [];

        var errors = new List<ShaderError>();
        var unmatched = new StringBuilder();

        foreach (string rawLine in fxcErrorText.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Match m = DiagnosticLine().Match(line);
            if (!m.Success)
            {
                unmatched.AppendLine(line);
                continue;
            }

            string file = m.Groups["file"].Value;
            int lineNum = int.Parse(m.Groups["line"].Value);
            int col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : 0;
            string severityText = m.Groups["severity"].Value;
            string code = m.Groups["code"].Value;
            string message = m.Groups["message"].Value;

            ShaderErrorSeverity severity = severityText == "warning"
                ? ShaderErrorSeverity.Warning
                : ShaderErrorSeverity.Error;

            if (string.Equals(file, sourceFileName, StringComparison.OrdinalIgnoreCase))
                file = sourceFileName;

            errors.Add(new ShaderError(
                File: file,
                Line: lineNum,
                Column: col,
                Code: code,
                Message: message,
                Severity: severity,
                RawDiagnostics: line));
        }

        if (unmatched.Length > 0 && errors.Count == 0)
        {
            // Constraint 5: text that doesn't parse into file(line,col) entries is
            // surfaced VERBATIM as the message — never the old fixed "Shader
            // compilation failed" with the real text hidden in RawDiagnostics.
            // When located diagnostics DID parse, the unmatched remainder is their
            // context and rides on the complete raw text SelectPrimary attaches.
            string verbatim = unmatched.ToString().TrimEnd();
            errors.Add(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "X0000",
                Message: verbatim,
                Severity: ShaderErrorSeverity.Error,
                RawDiagnostics: verbatim));
        }

        return errors;
    }

    /// <summary>
    /// Selects the single primary <see cref="ShaderError"/> for a FAILED compile: the
    /// first <see cref="ShaderErrorSeverity.Error"/>-severity entry (fxc/vkd3d print
    /// warnings before the fatal diagnostic), else the first entry — with
    /// <see cref="ShaderError.RawDiagnostics"/> always set to the COMPLETE text so the
    /// single-error backend contract loses nothing. Falls back to
    /// <paramref name="noDiagnosticsFallback"/> (code <paramref name="fallbackCode"/>)
    /// only when the compiler emitted no text at all.
    /// </summary>
    public static ShaderError SelectPrimary(
        string errorText,
        string sourceFileName,
        string noDiagnosticsFallback,
        string fallbackCode = "X0000")
    {
        IReadOnlyList<ShaderError> errors = Reformat(errorText, sourceFileName);
        string? raw = string.IsNullOrWhiteSpace(errorText) ? null : errorText.TrimEnd();

        ShaderError? primary = null;
        foreach (ShaderError e in errors)
        {
            if (e.Severity == ShaderErrorSeverity.Error)
            {
                primary = e;
                break;
            }
        }
        primary ??= errors.Count > 0 ? errors[0] : null;

        if (primary is not null)
            return primary with { RawDiagnostics = raw ?? primary.RawDiagnostics };

        return new ShaderError(
            File: sourceFileName,
            Line: 0,
            Column: 0,
            Code: fallbackCode,
            Message: noDiagnosticsFallback,
            Severity: ShaderErrorSeverity.Error,
            RawDiagnostics: raw);
    }

    /// <summary>
    /// Parses the diagnostic text of a SUCCESSFUL compile into warning diagnostics.
    /// A successful compile cannot carry errors, so an unlocated verbatim entry is
    /// normalized to <see cref="ShaderErrorSeverity.Warning"/> — the text stays
    /// verbatim.
    /// </summary>
    public static IReadOnlyList<ShaderError> ReformatAsWarnings(
        string errorText,
        string sourceFileName)
    {
        IReadOnlyList<ShaderError> parsed = Reformat(errorText, sourceFileName);
        if (parsed.Count == 0)
            return parsed;

        var warnings = new List<ShaderError>(parsed.Count);
        foreach (ShaderError e in parsed)
        {
            warnings.Add(e.Severity == ShaderErrorSeverity.Error
                ? e with { Severity = ShaderErrorSeverity.Warning }
                : e);
        }
        return warnings;
    }
}
