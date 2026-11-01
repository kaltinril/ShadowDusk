#nullable enable

using System.Text;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Refuses a <c>#pragma pack_matrix</c> whose argument is not <c>row_major</c> or
/// <c>column_major</c> before DXC sees the source (issue #452).
/// </summary>
/// <remarks>
/// <para><b>Measured on the pinned DXC.</b> Compiled one at a time, every other argument
/// (<c>bogus</c>, <c>ROW_MAJOR</c>, an empty <c>()</c>, a number, two arguments, an unclosed or a
/// missing parenthesis, a macro that expands to anything else, and the <c>_Pragma("pack_matrix(...)")</c>
/// operator form) is DXC's error, on DirectX 12, OpenGL and Vulkan alike, with a message
/// whose expected-token text is visibly missing ("invalid argument; expected  or 'disable'",
/// "expected '}'"): DXC formats the diagnostic from state it did not set. Under parallel load the
/// same source sometimes crashed the process inside DXC (issue #452: 8 of 200 runs on DirectX 12),
/// and <c>ROW_MAJOR</c> crashed an OpenGL compile on a sequential run too. So the source is refused
/// with the same severity DXC gives it (an error, warnings-as-errors or not), and DXC never reports
/// it.</para>
/// <para><b>What counts.</b> Only an ACTIVE directive (one in a dead <c>#if</c> branch never reaches
/// DXC's pragma handler) with its macros expanded, as DXC expands them, read from the managed
/// preprocessor's view of the source with the compile's own <c>-D</c> macros defined first. A
/// valid argument may be followed by anything (DXC compiles <c>pack_matrix(row_major) junk</c>).
/// A source the managed preprocessor cannot read is left to DXC, as before.</para>
/// </remarks>
internal static class DxcPackMatrixGuard
{
    /// <summary>The registered diagnostic code.</summary>
    public const string ErrorCode = "SD0241";

    /// <summary>The refusal for <paramref name="hlslSource"/>, or null when DXC may compile it.</summary>
    public static ShaderError? Check(
        string hlslSource, string? sourceFileName, IReadOnlyList<(string Name, string? Value)> macros)
    {
        // Cheap gate: nothing names the pragma, so there is nothing to read.
        if (!hlslSource.Contains("pack_matrix", StringComparison.Ordinal) &&
            !macros.Any(m => m.Value?.Contains("pack_matrix", StringComparison.Ordinal) == true))
        {
            return null;
        }

        string file = sourceFileName ?? string.Empty;

        // The -D macros come first, as DXC defines them before the source.
        var prefix = new StringBuilder();
        foreach ((string name, string? value) in macros)
            prefix.Append("#define ").Append(name).Append(' ').Append(value ?? "1").Append('\n');
        int prefixLines = macros.Count;

        Result<FxMacroPreprocessor.InputGuardView, ShaderError> view =
            FxMacroPreprocessor.ProcessForInputGuards(prefix + hlslSource, file);
        if (view.IsFailure)
            return null;

        var lineMap = new SourceLineMap(hlslSource, file);
        foreach (FxMacroPreprocessor.PackMatrixPragma pragma in view.Value.PackMatrixPragmas)
        {
            if (IsValid(pragma.Tokens))
                continue;
            (string atFile, int atLine) = lineMap.Resolve(pragma.PhysicalLine - prefixLines);
            return Error(atFile, atLine, pragma.Column, pragma.Tokens);
        }

        foreach ((string text, int line, int column) in FxMacroPreprocessor.FindPragmaOperators(view.Value.View))
        {
            IReadOnlyList<string> tokens = FxMacroPreprocessor.TokenTexts(text);
            if (tokens.Count == 0 || tokens[0] != "pack_matrix")
                continue;
            IReadOnlyList<string> arguments = tokens.Skip(1).ToList();
            if (IsValid(arguments))
                continue;
            (string atFile, int atLine) = lineMap.Resolve(line - prefixLines);
            return Error(atFile, atLine, column, arguments);
        }

        return null;
    }

    private static bool IsValid(IReadOnlyList<string> tokens) =>
        tokens.Count >= 3 &&
        tokens[0] == "(" &&
        tokens[1] is "row_major" or "column_major" &&
        tokens[2] == ")";

    private static ShaderError Error(string file, int line, int column, IReadOnlyList<string> tokens)
    {
        string written = tokens.Count == 0 ? "nothing" : $"'{string.Join(' ', tokens)}'";
        return new ShaderError(
            File: file,
            Line: line,
            Column: column,
            Code: ErrorCode,
            Message: $"'#pragma pack_matrix' takes '(row_major)' or '(column_major)', and here it is given {written} " +
                     "(after macro expansion). DXC rejects any other argument as an error, and the pinned DXC can " +
                     "crash the whole process while reporting it, so ShadowDusk refuses it before DXC compiles. " +
                     "Write '#pragma pack_matrix(row_major)' or '#pragma pack_matrix(column_major)', or remove the line.");
    }
}
