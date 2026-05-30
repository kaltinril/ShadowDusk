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

        if (unmatched.Length > 0)
        {
            errors.Add(new ShaderError(
                File: sourceFileName,
                Line: 0,
                Column: 0,
                Code: "X0000",
                Message: "Shader compilation failed",
                Severity: ShaderErrorSeverity.Error,
                RawDiagnostics: unmatched.ToString().TrimEnd()));
        }

        return errors;
    }
}
