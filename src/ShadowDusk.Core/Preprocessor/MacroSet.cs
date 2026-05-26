#nullable enable

using System.Text;

namespace ShadowDusk.Core.Preprocessor;

public sealed record MacroSet(IReadOnlyList<MacroDefinition> Macros)
{
    public string ToTextPrepend(string originalFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// ShadowDusk platform macros — DO NOT EDIT (generated)");
        foreach (var macro in Macros)
            sb.AppendLine($"#define {macro.Name} {macro.Value}");
        sb.AppendLine($"#line 1 \"{originalFilePath}\"");
        return sb.ToString();
    }

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
