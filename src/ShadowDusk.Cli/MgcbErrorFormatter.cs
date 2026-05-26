#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

internal static class MgcbErrorFormatter
{
    public static string Format(ShaderError error)
    {
        string severity = error.Severity == ShaderErrorSeverity.Warning ? "warning" : "error";
        string code = FormatCode(error.Code);
        string filename = Path.GetFileName(error.File);

        if (error.Line > 0)
            return $"{filename}({error.Line},{error.Column}-{error.Column}): {severity} {code}: {error.Message}";

        return $"{severity} {code}: {error.Message}";
    }

    public static IEnumerable<string> FormatAll(IEnumerable<ShaderError> errors)
    {
        foreach (var error in errors)
            yield return Format(error);
    }

    // If the code already matches X followed by exactly 4 digits, pass it through unchanged.
    // If it is a raw integer string, zero-pad to 4 digits and prefix with X.
    // Anything else (e.g. "SD0001") is passed through as-is.
    private static string FormatCode(string code)
    {
        if (code.Length == 5 &&
            (code[0] == 'X' || code[0] == 'x') &&
            code[1..].All(char.IsAsciiDigit))
        {
            return code[0] == 'X' ? code : "X" + code[1..];
        }

        if (int.TryParse(code, out int numericCode))
            return $"X{numericCode:D4}";

        return code;
    }
}
