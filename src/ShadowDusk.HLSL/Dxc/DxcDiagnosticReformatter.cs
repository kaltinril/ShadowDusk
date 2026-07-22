#nullable enable

using System.Text;
using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

internal static partial class DxcDiagnosticReformatter
{
    // DXC emits Clang-style diagnostics: <file>:<line>:<col>: <severity>: <message>
    // The file part may contain a drive letter with colon on Windows, so we match
    // greedily up to the last occurrence of ":\d+:\d+:" rather than splitting on
    // the first colon.
    [GeneratedRegex(
        @"^(?<file>.+):(?<line>\d+):(?<col>\d+):\s*(?<severity>error|warning|note):\s*(?<message>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticLine();

    public static IReadOnlyList<ShaderError> Reformat(string dxcErrorText, string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(dxcErrorText))
            return [];

        var errors = new List<ShaderError>();
        var unmatched = new StringBuilder();

        foreach (string rawLine in dxcErrorText.Split('\n'))
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
            int col = int.Parse(m.Groups["col"].Value);
            string severityText = m.Groups["severity"].Value;
            string message = m.Groups["message"].Value;

            ShaderErrorSeverity severity = severityText switch
            {
                "error" => ShaderErrorSeverity.Error,
                "warning" => ShaderErrorSeverity.Warning,
                _ => ShaderErrorSeverity.Note
            };

            // Normalise the file path: DXC may echo back the source name we gave it;
            // if it matches the request's file name use that for consistency.
            if (string.Equals(file, sourceFileName, StringComparison.OrdinalIgnoreCase))
                file = sourceFileName;

            errors.Add(new ShaderError(
                File: file,
                Line: lineNum,
                Column: col,
                Code: "X0000",
                Message: message,
                Severity: severity,
                RawDiagnostics: line));
        }

        if (unmatched.Length > 0 && errors.Count == 0)
        {
            // Constraint 5 (and the 2026-07 field reports): never replace the
            // compiler's words with a generic sentence. DXC text with no
            // file:line:col prefix (SPIR-V codegen/legalization failures, internal
            // errors — disproportionately the OpenGL leg) used to collapse into a
            // fixed "Shader compilation failed" whose real text hid in
            // RawDiagnostics, which no delivery surface printed. The verbatim text
            // IS the message now. When located diagnostics DID parse, the unmatched
            // remainder is their source-echo/caret context, not a diagnostic of its
            // own — it reaches the user via the complete raw text
            // <see cref="SelectPrimary"/> attaches, never as a fabricated entry.
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
    /// Selects the single primary <see cref="ShaderError"/> for a FAILED compile from
    /// the complete diagnostic text: the first
    /// <see cref="ShaderErrorSeverity.Error"/>-severity entry (DXC prints warnings
    /// before the error that failed the compile, and a warning must never masquerade
    /// as the failure), else the first entry — with
    /// <see cref="ShaderError.RawDiagnostics"/> always set to the COMPLETE text, so
    /// the single-error backend contract loses nothing (the delivery surfaces print
    /// it). Falls back to <paramref name="noDiagnosticsFallback"/> (code
    /// <paramref name="fallbackCode"/>) only when the compiler emitted no text at all.
    /// </summary>
    public static ShaderError SelectPrimary(
        string dxcErrorText,
        string sourceFileName,
        string noDiagnosticsFallback,
        string fallbackCode = "X0000")
    {
        IReadOnlyList<ShaderError> errors = Reformat(dxcErrorText, sourceFileName);
        string? raw = string.IsNullOrWhiteSpace(dxcErrorText) ? null : dxcErrorText.TrimEnd();

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
    /// A successful compile cannot carry errors, so an unlocated verbatim entry (text
    /// with no parseable location) is normalized to
    /// <see cref="ShaderErrorSeverity.Warning"/> — the text itself stays verbatim.
    /// </summary>
    public static IReadOnlyList<ShaderError> ReformatAsWarnings(
        string dxcErrorText,
        string sourceFileName)
    {
        IReadOnlyList<ShaderError> parsed = Reformat(dxcErrorText, sourceFileName);
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
