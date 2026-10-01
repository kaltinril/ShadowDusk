#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// AppendLine, Environment.NewLine and TextWriter.WriteLine emit the HOST newline (CRLF on Windows), so
/// text that becomes compiler input or shipped output would differ per host. Producers must append '\n'.
/// Diagnostics and console formatting (Core, HLSL reformatters, Cli, MgcbPlugin) are exempt by project.
/// </summary>
public sealed class HostNewlineBanTests
{
    private static readonly string[] GeneratorProjects =
    [
        "ShadowDusk.Compiler", "ShadowDusk.GLSL", "ShadowDusk.Metal", "ShadowDusk.ShaderToy",
        "ShadowDusk.Slang", "ShadowDusk.Wasm", "ShadowDusk.ContentPipeline",
    ];

    private static readonly Regex Banned = new(
        @"\.AppendLine\s*\(|Environment\.NewLine|\bWriteLine(Async)?\s*\(|\.NewLine\b", RegexOptions.Compiled);

    [Fact]
    public void GeneratorProjects_NeverUseHostNewline()
    {
        string src = Path.Combine(FindRepoRoot(), "src");
        var offenders = new List<string>();
        char sep = Path.DirectorySeparatorChar;

        foreach (string project in GeneratorProjects)
        {
            string dir = Path.Combine(src, project);
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}"))
                    continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i].TrimStart();
                    if (code.StartsWith("//") || code.StartsWith('*')) continue;
                    if (Banned.IsMatch(lines[i]))
                        offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {code}");
                }
            }
        }

        offenders.ShouldBeEmpty("generated text must append '\\n' explicitly:\n" + string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ShadowDusk.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("ShadowDusk.slnx not found above the test binaries.");
    }
}
