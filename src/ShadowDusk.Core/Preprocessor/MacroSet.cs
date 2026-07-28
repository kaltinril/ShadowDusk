#nullable enable

using System.Text;

namespace ShadowDusk.Core.Preprocessor;

/// <summary>
/// A set of preprocessor macros applied to a compile, with helpers to render them either as
/// prepended <c>#define</c> text or as DXC command-line <c>-D</c> flags.
/// </summary>
/// <param name="Macros">The macro definitions in the set.</param>
public sealed record MacroSet(IReadOnlyList<MacroDefinition> Macros)
{
    /// <summary>
    /// User-supplied macros (mgfxc's <c>/Defines:</c>, via <c>CompilerOptions.Defines</c>),
    /// rendered after <see cref="Macros"/> by both renderers so every backend sees them.
    /// Empty by default.
    /// </summary>
    public IReadOnlyList<UserDefine> UserDefines { get; init; } = [];

    /// <summary>
    /// Renders the macros as a header block of <c>#define</c> lines followed by a
    /// <c>#line 1</c> directive that restores the original file path for diagnostics.
    /// </summary>
    /// <param name="originalFilePath">The source path to restore via <c>#line</c>.</param>
    /// <returns>Text to prepend to the shader source.</returns>
    public string ToTextPrepend(string originalFilePath)
    {
        // '\n', not AppendLine: the flattened body uses '\n', and Environment.NewLine
        // here made the compiler input differ by build OS (CRLF-mixed on Windows) —
        // visible in debug-mode artifacts via embedded source (bug-hunt 2026-07-27 N17).
        var sb = new StringBuilder();
        sb.Append("// ShadowDusk platform macros — DO NOT EDIT (generated)\n");
        foreach (var macro in Macros)
            sb.Append($"#define {macro.Name} {macro.Value}\n");
        foreach (var define in UserDefines)
            sb.Append($"#define {define.Name} {define.Value}\n");
        sb.Append($"#line 1 \"{originalFilePath.Replace('\\', '/')}\"\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the macros as DXC command-line define flags (<c>-D NAME=VALUE</c> pairs).
    /// </summary>
    /// <returns>The flattened flag list to pass to DXC.</returns>
    public IReadOnlyList<string> ToDxcFlags()
    {
        var flags = new List<string>((Macros.Count + UserDefines.Count) * 2);
        foreach (var macro in Macros)
        {
            flags.Add("-D");
            flags.Add($"{macro.Name}={macro.Value}");
        }
        foreach (var define in UserDefines)
        {
            flags.Add("-D");
            flags.Add($"{define.Name}={define.Value}");
        }
        return flags;
    }
}
