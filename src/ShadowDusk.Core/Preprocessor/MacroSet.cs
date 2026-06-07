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
    /// Renders the macros as a header block of <c>#define</c> lines followed by a
    /// <c>#line 1</c> directive that restores the original file path for diagnostics.
    /// </summary>
    /// <param name="originalFilePath">The source path to restore via <c>#line</c>.</param>
    /// <returns>Text to prepend to the shader source.</returns>
    public string ToTextPrepend(string originalFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ShadowDusk platform macros — DO NOT EDIT (generated)");
        foreach (var macro in Macros)
            sb.AppendLine($"#define {macro.Name} {macro.Value}");
        sb.AppendLine($"#line 1 \"{originalFilePath.Replace('\\', '/')}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the macros as DXC command-line define flags (<c>-D NAME=VALUE</c> pairs).
    /// </summary>
    /// <returns>The flattened flag list to pass to DXC.</returns>
    public IReadOnlyList<string> ToDxcFlags()
    {
        var flags = new List<string>(Macros.Count * 2);
        foreach (var macro in Macros)
        {
            flags.Add("-D");
            flags.Add($"{macro.Name}={macro.Value}");
        }
        return flags;
    }
}
