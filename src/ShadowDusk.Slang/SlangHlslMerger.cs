#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.Slang;

/// <summary>
/// Merges one or more slangc <c>-target hlsl</c> emissions (one per discovered entry point)
/// into a single HLSL body for the synthesized <c>.fx</c> technique.
///
/// <para><b>Why a merge step exists at all:</b> slangc -target hlsl emits a fully
/// self-contained translation unit PER ENTRY POINT: every struct/cbuffer/resource it
/// actually uses is redeclared in full, not imported/shared across invocations. A VS+PS
/// pair's two translation units can't just be concatenated: HLSL rejects a type redeclared
/// with the same name twice, even when the two declarations are identical text. The merge
/// collapses identical top-level declarations to one copy and keeps each entry's own
/// function definition.</para>
///
/// <para><b>Top-level declarations, not <c>#line</c> fragments (issue #228):</b> slangc also
/// threads <c>#line</c> directives INSIDE function bodies, so splitting at every <c>#line</c>
/// (the original shape of this class) cut functions into fragments and could drop a
/// fragment of one function because an identical fragment had been seen in another. The
/// unit is now split by brace depth into whole top-level declarations (struct, function,
/// cbuffer, resource, static global), and only whole declarations are compared.</para>
///
/// <para><b>Name collisions across units (issue #228):</b> slangc numbers symbols per
/// translation unit (<c>helper_0</c>, <c>Box_0</c>, <c>apply_1</c>, ...) even with
/// <c>-no-mangle</c>, and numbers generic instantiations in first-use order. Two entries
/// that instantiate the same generic with different type arguments in a different order
/// therefore produce the SAME name for DIFFERENT bodies (measured: <c>helper_0</c> is the
/// <c>float</c> instantiation in the vertex unit and the <c>float2</c> one in the pixel
/// unit). Left alone that is a downstream redefinition error at best and, when the
/// signatures differ, a silently-legal HLSL overload at worst. Every struct, function and
/// static global whose name was already declared with a different body is renamed in the
/// later unit (<c>name_e1</c>), consistently across that whole unit, iterated to a fixed
/// point because a declaration that merely REFERENCES a renamed symbol changes text too.
/// cbuffers, textures and samplers are never renamed (their names are the effect's reflected
/// parameter table); a same-named, differently-bodied one of those cannot be fixed by
/// renaming and is reported as a conflict instead.</para>
/// </summary>
internal static class SlangHlslMerger
{
    /// <summary>A same-named, differently-bodied declaration that renaming cannot fix.</summary>
    internal readonly record struct Conflict(string Name, int UnitIndex);

    private enum DeclKind { Other, Renamable, Fixed }

    private sealed record Decl(string Text, string? Name, DeclKind Kind);

    private static readonly Regex LineDirective = new(
        @"^#line\b.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex LeadingAttributes = new(
        @"^(?:\s*\[[^\]]*\])+", RegexOptions.Compiled);

    private static readonly Regex StructHeader = new(
        @"^(?:struct|class)\s+(\w+)", RegexOptions.Compiled);

    private static readonly Regex BufferHeader = new(
        @"^(?:cbuffer|tbuffer)\s+(\w+)", RegexOptions.Compiled);

    private static readonly Regex LastIdentifier = new(
        @"(\w+)\s*$", RegexOptions.Compiled);

    private static readonly Regex Identifier = new(
        @"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

    /// <summary>
    /// Merges the per-entry-point HLSL translation units slangc produced, in the order
    /// given. Convenience form for callers that do not need conflict reporting: an
    /// unresolvable conflict leaves both declarations in place (the downstream compiler's
    /// own redefinition error is then the loud result), never a silent pick of one.
    /// </summary>
    public static string Merge(IReadOnlyList<string> perEntryHlsl) =>
        TryMerge(perEntryHlsl, [], out _);

    /// <summary>
    /// Merges the units, deduplicating identical top-level declarations and renaming
    /// colliding generated names (see the class remarks). <paramref name="reservedNames"/>
    /// (the entry-point names) are never renamed. <paramref name="conflicts"/> lists any
    /// collision renaming cannot resolve; both declarations are still emitted.
    /// </summary>
    public static string TryMerge(
        IReadOnlyList<string> perEntryHlsl,
        IReadOnlyCollection<string> reservedNames,
        out IReadOnlyList<Conflict> conflicts)
    {
        var found = new List<Conflict>();
        conflicts = found;

        if (perEntryHlsl.Count == 0)
            return "";
        if (perEntryHlsl.Count == 1)
            return perEntryHlsl[0];

        var sb = new StringBuilder();
        // Ordinal: these keys are generated HLSL text, never user-facing/locale-sensitive.
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var anonymous = new HashSet<string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (string unit in perEntryHlsl)
        {
            foreach (Match id in Identifier.Matches(unit))
                taken.Add(id.Value);
        }

        for (int u = 0; u < perEntryHlsl.Count; u++)
        {
            string unit = perEntryHlsl[u];
            Match first = LineDirective.Match(unit);
            int preludeEnd = first.Success ? first.Index : unit.Length;

            // The shared pragma pack_matrix / NVAPI-guard header is identical across every
            // entry output (same slangc flags, same target): keep the first copy only.
            if (u == 0)
                sb.Append(unit, 0, preludeEnd);

            List<Decl> decls = SplitTopLevel(unit[preludeEnd..]);
            Dictionary<string, string> renames = ResolveRenames(
                decls, declared, taken, reservedNames, u, found);

            foreach (Decl decl in decls)
            {
                string text = renames.Count == 0 ? decl.Text : ApplyRenames(decl.Text, renames);
                string key = Normalize(text);

                if (key.Length == 0)
                {
                    sb.Append(text);
                    continue;
                }

                if (decl.Name is null)
                {
                    if (anonymous.Add(key) || u == 0)
                        sb.Append(text);
                    continue;
                }

                string name = renames.TryGetValue(decl.Name, out string? renamed) ? renamed : decl.Name;
                if (!declared.TryGetValue(name, out HashSet<string>? keys))
                    declared[name] = keys = new HashSet<string>(StringComparer.Ordinal);

                // Identical to an earlier declaration of this name: already emitted.
                if (keys.Add(key) || u == 0)
                    sb.Append(text);
            }
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ResolveRenames(
        List<Decl> decls,
        Dictionary<string, HashSet<string>> declared,
        HashSet<string> taken,
        IReadOnlyCollection<string> reservedNames,
        int unitIndex,
        List<Conflict> conflicts)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (unitIndex == 0)
            return renames;

        var reported = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (Decl decl in decls)
            {
                if (decl.Name is null || renames.ContainsKey(decl.Name))
                    continue;
                if (!declared.TryGetValue(decl.Name, out HashSet<string>? keys))
                    continue;

                string key = Normalize(renames.Count == 0 ? decl.Text : ApplyRenames(decl.Text, renames));
                if (key.Length == 0 || keys.Contains(key))
                    continue;

                if (decl.Kind == DeclKind.Renamable && !ContainsName(reservedNames, decl.Name))
                {
                    string candidate = decl.Name + "_e" + unitIndex;
                    while (taken.Contains(candidate))
                        candidate += "_";
                    taken.Add(candidate);
                    renames[decl.Name] = candidate;
                    changed = true;
                }
                else if (reported.Add(decl.Name))
                {
                    conflicts.Add(new Conflict(decl.Name, unitIndex));
                }
            }
        }
        while (changed);

        return renames;
    }

    private static bool ContainsName(IReadOnlyCollection<string> names, string name)
    {
        foreach (string n in names)
        {
            if (string.Equals(n, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // One simultaneous pass over identifiers, so a renamed target can never be re-renamed.
    private static string ApplyRenames(string text, Dictionary<string, string> renames) =>
        Identifier.Replace(text, m => renames.TryGetValue(m.Value, out string? to) ? to : m.Value);

    // Comparison key: the declaration text without #line bookkeeping, blank lines or
    // trailing whitespace, so two entries redeclaring the same thing under different
    // #line numbers still collapse.
    private static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("#line", StringComparison.Ordinal))
                continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits a unit text (everything after the shared prelude) into whole top-level
    /// declarations by brace depth. Lossless: the pieces concatenate back to the input.
    /// A declaration ends at the line where brace depth returns to zero after a closing
    /// brace, or at a semicolon seen at depth zero, and absorbs the blank lines after it;
    /// a leading #line belongs to the next one.
    /// </summary>
    private static List<Decl> SplitTopLevel(string text)
    {
        var result = new List<Decl>();
        int depth = 0;
        int start = 0;
        bool sawBrace = false;
        int pos = 0;

        while (pos < text.Length)
        {
            int lineEnd = text.IndexOf('\n', pos);
            int lineStop = lineEnd < 0 ? text.Length : lineEnd;
            int next = lineEnd < 0 ? text.Length : lineEnd + 1;
            string line = text[pos..lineStop];

            if (!line.TrimStart().StartsWith('#'))
            {
                int comment = line.IndexOf("//", StringComparison.Ordinal);
                string code = comment >= 0 ? line[..comment] : line;
                foreach (char c in code)
                {
                    if (c == '{')
                    {
                        depth++;
                        sawBrace = true;
                    }
                    else if (c == '}')
                    {
                        depth--;
                    }
                }

                bool endsHere = depth <= 0 &&
                    ((sawBrace && code.Contains('}')) || (!sawBrace && code.TrimEnd().EndsWith(';')));
                if (endsHere)
                {
                    next = SkipBlankLines(text, next);
                    result.Add(MakeDecl(text[start..next]));
                    start = next;
                    depth = 0;
                    sawBrace = false;
                }
            }

            pos = next;
        }

        if (start < text.Length)
            result.Add(MakeDecl(text[start..]));
        return result;
    }

    private static int SkipBlankLines(string text, int from)
    {
        int scan = from;
        while (scan < text.Length)
        {
            int le = text.IndexOf('\n', scan);
            int stop = le < 0 ? text.Length : le;
            if (text[scan..stop].Trim().Length != 0)
                break;
            scan = le < 0 ? text.Length : le + 1;
        }
        return scan;
    }

    private static Decl MakeDecl(string text)
    {
        var analysis = new StringBuilder();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment];
            if (line.TrimStart().StartsWith('#'))
                continue;
            analysis.Append(line).Append('\n');
        }

        string t = analysis.ToString().Trim();
        if (t.Length == 0)
            return new Decl(text, null, DeclKind.Other);

        t = LeadingAttributes.Replace(t, "").TrimStart();
        int brace = t.IndexOf('{');
        string header = (brace >= 0 ? t[..brace] : t).Trim();

        Match m = StructHeader.Match(header);
        if (m.Success)
            return new Decl(text, m.Groups[1].Value, DeclKind.Renamable);

        m = BufferHeader.Match(header);
        if (m.Success)
            return new Decl(text, m.Groups[1].Value, DeclKind.Fixed);

        // A function has its open paren before any colon, equals or semicolon (a resource
        // register(t0) and a global initializer call both come after one of them).
        int paren = header.IndexOf('(');
        int stop = header.Length;
        foreach (char c in new[] { ':', '=', ';' })
        {
            int i = header.IndexOf(c);
            if (i >= 0 && i < stop)
                stop = i;
        }

        if (paren >= 0 && paren < stop)
        {
            Match fn = LastIdentifier.Match(header[..paren]);
            return fn.Success
                ? new Decl(text, fn.Groups[1].Value, DeclKind.Renamable)
                : new Decl(text, null, DeclKind.Other);
        }

        // A variable, resource or typedef: its name is the last identifier before the first
        // colon, equals, semicolon or array bracket.
        string varHeader = header[..stop];
        int bracket = varHeader.IndexOf('[');
        if (bracket >= 0)
            varHeader = varHeader[..bracket];
        Match v = LastIdentifier.Match(varHeader);
        if (!v.Success)
            return new Decl(text, null, DeclKind.Other);

        bool renamable = header.StartsWith("static", StringComparison.Ordinal) ||
                         header.StartsWith("typedef", StringComparison.Ordinal);
        return new Decl(text, v.Groups[1].Value, renamable ? DeclKind.Renamable : DeclKind.Fixed);
    }
}
