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
