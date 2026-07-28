#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Cli;

internal static class MgcbErrorFormatter
{
    public static string Format(ShaderError error)
    {
        string severity = error.Severity switch
        {
            ShaderErrorSeverity.Warning => "warning",
            ShaderErrorSeverity.Note    => "note",
            _                           => "error",
        };
        string code = FormatCode(error.Code);
        // The path exactly as the compiler was given it — never just the file name
        // (bug-hunt 2026-07-27 N15): two same-named includes from different directories
        // must stay distinguishable, and IDE/MSBuild jump-to-file needs the directory.
        // fxc/mgfxc echo the path they were given the same way.
        string filename = error.File;

        if (error.Line > 0)
            return $"{filename}({error.Line},{error.Column}-{error.Column}): {severity} {code}: {error.Message}";

        // File-scoped but line-less: still lead with the file. The GL portability lint
        // (SD0400-SD0402) derives its findings from the EMITTED GLSL, which has no line
        // mapping back to the .fx, so those warnings carry a file and no line. Without
        // this branch an MGCB build compiling many effects printed a bare
        // "warning SD0401: ... pixel shader 'MainPS' ..." with no way to tell WHICH
        // effect it came from (and 'MainPS' is a near-universal entry-point name).
        if (!string.IsNullOrEmpty(filename))
            return $"{filename}: {severity} {code}: {error.Message}";

        return $"{severity} {code}: {error.Message}";
    }

    public static IEnumerable<string> FormatAll(IEnumerable<ShaderError> errors)
    {
        foreach (var error in errors)
        {
            // Message is deliberately verbatim and CAN be multi-line (an unparseable
            // compiler blob becomes the message). Emit line 1 as the parseable diagnostic and
            // indent the rest, matching how the raw block below is presented, so a reader can
            // see at a glance which lines belong to one diagnostic. Note this is presentation,
            // not a guarantee: MSBuild's canonical-diagnostic regex is anchored `^\s*`, so
            // indentation alone does not stop an MSBuild-family parser from picking up a
            // continuation line that happens to look like a diagnostic.
            string formatted = Format(error);
            string[] lines = formatted.Replace("\r\n", "\n").Split('\n');
            yield return lines[0];
            for (int i = 1; i < lines.Length; i++)
                yield return "    " + lines[i];

            // The underlying compiler's COMPLETE output, whenever it says more than
            // the one-line summary (constraint 5: the compiler's own words, shown by
            // default — no verbose flag to find). Indented so MGCB's line parser
            // cannot mistake it for a second diagnostic; the parseable line above
            // stays first and unchanged.
            if (error.HasAdditionalRawDiagnostics)
            {
                foreach (string raw in error.RawDiagnostics!.TrimEnd().Replace("\r\n", "\n").Split('\n'))
                    yield return "    " + raw;
            }
        }
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
