#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Slang;

/// <summary>The shader stage of a discovered Slang entry point, restricted to what an <c>Effect</c> can hold.</summary>
internal enum SlangStage
{
    Vertex,
    Fragment,
}

/// <summary>One entry point discovered in the user's <c>.slang</c> source.</summary>
/// <param name="Name">The function name.</param>
/// <param name="Stage">Its stage.</param>
/// <param name="Line">1-based line of the <c>[shader(...)]</c> attribute, for diagnostics.</param>
internal sealed record SlangEntryPoint(string Name, SlangStage Stage, int Line);

/// <summary>
/// Finds the entry points in a <c>.slang</c> source by its own idiom: the
/// <c>[shader("stage")]</c> attribute. Slang has no <c>technique</c>/<c>pass</c> concept
/// (measured: Slang's own compiler errors on the FX9 block of any real <c>.fx</c>), so the attributes are
/// the only authoritative statement of what the entry points are, and the frontend synthesizes
/// the technique from them.
///
/// <para>Stage policy is Phase 61 A6's three bands applied to stages: <c>vertex</c> and
/// <c>fragment</c>/<c>pixel</c> pass through (the two stages MonoGame's and KNI's
/// <c>Effect</c> can hold, Phase 58's measurement); every other stage Slang defines
/// (<c>compute</c>, <c>mesh</c>, the raytracing set, …) is rejected <b>loudly, by name</b>
/// (<c>SD0602</c>) — a Slang author has every reason to expect a compute entry point to
/// work, so a silent skip would be exactly the wrong-output class this project refuses.</para>
/// </summary>
internal static class SlangEntryScanner
{
    // [shader("stage")] optionally spread over whitespace, then (skipping any further
    // attributes) the function header: its return type tokens and the name before '('.
    private static readonly Regex ShaderAttribute = new(
        """\[\s*shader\s*\(\s*"(?<stage>[a-z]+)"\s*\)\s*\]\s*(?:\[[^\]]*\]\s*)*(?<header>[^;{(]*?)(?<name>[A-Za-z_]\w*)\s*\(""",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="slangSource"/> and returns the vertex/fragment entry points, or the
    /// loud diagnostics for anything outside what a synthesized technique can hold.
    /// </summary>
    public static Result<IReadOnlyList<SlangEntryPoint>, ShaderError[]> Scan(
        string slangSource, string sourceName)
    {
        var entries = new List<SlangEntryPoint>();
        var errors = new List<ShaderError>();

        foreach (Match m in ShaderAttribute.Matches(slangSource))
        {
            int line = 1 + slangSource.AsSpan(0, m.Index).Count('\n');
            string stage = m.Groups["stage"].Value;
            string name = m.Groups["name"].Value;

            switch (stage)
            {
                case "vertex":
                    entries.Add(new SlangEntryPoint(name, SlangStage.Vertex, line));
                    break;

                // Slang's canonical name is "fragment"; "pixel" is accepted as the HLSL-side alias.
                case "fragment" or "pixel":
                    entries.Add(new SlangEntryPoint(name, SlangStage.Fragment, line));
                    break;

                default:
                    errors.Add(new ShaderError(
                        File: sourceName, Line: line, Column: 1, Code: "SD0602",
                        Message: $"entry point '{name}' is [shader(\"{stage}\")], a stage no supported " +
                                 "consumer runtime can load: stock MonoGame and KNI Effects hold exactly " +
                                 "two stages, vertex and pixel. Remove or conditionally exclude this " +
                                 "entry point. (This is a runtime limit, not a ShadowDusk gap — no " +
                                 "compiler can produce a loadable Effect containing it.)"));
                    break;
            }
        }

        // >1 entry per stage: v1 is deliberately strict. Multiple techniques from one .slang is a
        // real design (a convention for grouping entries into techniques would be needed);
        // guessing one would be silent wrong output.
        foreach (var group in entries.GroupBy(e => e.Stage).Where(g => g.Count() > 1))
        {
            errors.Add(new ShaderError(
                File: sourceName, Line: group.Skip(1).First().Line, Column: 1, Code: "SD0604",
                Message: $"multiple [shader(\"...\")] entry points for the {group.Key} stage " +
                         $"({string.Join(", ", group.Select(e => $"'{e.Name}'"))}); the Slang frontend " +
                         "synthesizes one technique with at most one vertex and one pixel entry. " +
                         "Split the extra entry points into separate .slang files."));
        }

        if (entries.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ShaderError(
                File: sourceName, Line: 0, Column: 0, Code: "SD0603",
                Message: "no [shader(\"vertex\")] / [shader(\"fragment\")] entry points found. Slang has " +
                         "no technique/pass concept, so the entry points must be marked with Slang's " +
                         "[shader(...)] attribute for ShadowDusk to synthesize the technique."));
        }

        return errors.Count > 0
            ? Result<IReadOnlyList<SlangEntryPoint>, ShaderError[]>.Fail(errors.ToArray())
            : Result<IReadOnlyList<SlangEntryPoint>, ShaderError[]>.Ok(entries);
    }
}
