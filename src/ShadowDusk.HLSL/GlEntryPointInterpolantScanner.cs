#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;
using ShadowDusk.HLSL.Lexer;

namespace ShadowDusk.HLSL;

/// <summary>
/// Reads, off the preprocessed tokens, the matrix declarations each function's signature carries
/// (<see cref="EntryPointMatrixInterpolants"/>), so the OpenGL rewrite can lay a matrix
/// interpolant out the way fxc does for THAT entry point: DXC's GLSL does not say whether a matrix
/// is <c>row_major</c>, and two techniques may declare the same semantic differently.
///
/// <para>Modelled: FILE-SCOPE struct definitions (members with comma declarators, arrays, nested
/// struct members with their own semantics, at any depth), file-scope <c>typedef</c>s of matrix
/// and struct types, function definitions at file scope with their return type and return
/// semantic and their parameters (<c>in</c>/<c>out</c>/<c>inout</c>, <c>uniform</c> skipped), and
/// the matrix types themselves (<c>floatRxC</c> and the other scalar families, <c>matrix</c>,
/// <c>matrix&lt;T, R, C&gt;</c>). A struct or typedef declared inside a function body is local to
/// it and never replaces the file-scope one; a type name defined twice at file scope is reported
/// unmodelled. The lexer drops <c>:</c> and <c>[ ]</c>, so a declarator is
/// <c>NAME [count] [SEMANTIC]</c>. A shape outside that is never guessed at: the rewriter refuses a
/// matrix interpolant it has no declaration for.</para>
///
/// <para>Expansion is memoized per struct and bounded (<see cref="MaxFlattened"/> declarations per
/// struct, <see cref="MaxDepth"/> levels), so a hostile nest of structs cannot make it exponential
/// (the browser fiddle compiles untrusted input).</para>
/// </summary>
internal static class GlEntryPointInterpolantScanner
{
    /// <summary>The deepest struct nesting expanded.</summary>
    internal const int MaxDepth = 16;

    /// <summary>The most matrix declarations one struct may flatten to.</summary>
    internal const int MaxFlattened = 1024;

    private static readonly Regex MatrixTypeName = new(
        @"^(?:float|half|double|min16float|min10float|int|uint|bool|dword|min16int|min16uint)([1-4])x([1-4])$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "in", "out", "inout", "uniform", "row_major", "column_major", "linear", "centroid",
        "nointerpolation", "noperspective", "sample", "const", "precise", "snorm", "unorm",
        "static", "inline", "extern",
    };

    private sealed record TypeRef(string Name, int Rows, int Cols, bool? RowMajor)
    {
        public bool IsMatrix => Rows > 0;
    }

    private sealed record Declarator(TypeRef Type, string Name, string? Semantic, bool? RowMajor, string Direction);

    private sealed record StructDef(List<Declarator> Members);

    /// <summary>A struct flattened: its matrix declarations with semantics, and what was not modelled.</summary>
    private sealed record Flattened(List<MatrixInterpolantDeclaration> Matrices, List<string> Unmodelled, bool HasMatrix);

    public static IReadOnlyDictionary<string, EntryPointMatrixInterpolants> Scan(IReadOnlyList<Token> tokens)
    {
        var code = tokens.Where(t => t.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.Preprocessor))
                         .ToList();
        var typedefs = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        var structs = new Dictionary<string, StructDef>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);    // defined twice at file scope
        var functions = new Dictionary<string, List<(TypeRef Ret, bool? RetMajor, string? RetSemantic, List<Declarator> Params)>>(StringComparer.Ordinal);

        bool TryType(int i, out TypeRef type, out int next)
        {
            type = new TypeRef("", 0, 0, null);
            next = i;
            if (i >= code.Count || code[i].Kind != TokenKind.Identifier) return false;
            string text = code[i].Text;
            Match m = MatrixTypeName.Match(text);
            if (m.Success)
            {
                type = new TypeRef(text, m.Groups[1].Value[0] - '0', m.Groups[2].Value[0] - '0', null);
                next = i + 1;
                return true;
            }
            if (typedefs.TryGetValue(text, out TypeRef? td))
            {
                type = td;
                next = i + 1;
                return true;
            }
            if (text == "matrix")
            {
                if (i + 7 < code.Count && code[i + 1].Kind == TokenKind.LAngle
                    && code[i + 3].Kind == TokenKind.Comma && code[i + 4].Kind == TokenKind.Number
                    && code[i + 5].Kind == TokenKind.Comma && code[i + 6].Kind == TokenKind.Number
                    && code[i + 7].Kind == TokenKind.RAngle
                    && int.TryParse(code[i + 4].Text, out int r) && int.TryParse(code[i + 6].Text, out int c))
                {
                    type = new TypeRef("matrix", r, c, null);
                    next = i + 8;
                    return true;
                }
                type = new TypeRef("matrix", 4, 4, null);
                next = i + 1;
                return true;
            }
            // Any other identifier is a scalar, vector or struct type name.
            type = new TypeRef(text, 0, 0, null);
            next = i + 1;
            return true;
        }

        // Modifiers starting at i: majorness and direction; returns the index of the type.
        int ReadModifiers(int i, int end, out bool? major, out string direction, out bool uniform)
        {
            major = null; direction = "in"; uniform = false;
            while (i < end && code[i].Kind == TokenKind.Identifier && Modifiers.Contains(code[i].Text))
            {
                switch (code[i].Text)
                {
                    case "row_major": major = true; break;
                    case "column_major": major = false; break;
                    case "out": direction = "out"; break;
                    case "inout": direction = "inout"; break;
                    case "uniform": uniform = true; break;
                }
                i++;
            }
            return i;
        }

        int MatchClose(int open, TokenKind openKind, TokenKind closeKind)
        {
            int depth = 0;
            for (int k = open; k < code.Count; k++)
            {
                if (code[k].Kind == openKind) depth++;
                else if (code[k].Kind == closeKind && --depth == 0) return k;
            }
            return -1;
        }

        void Define(string name, Action define)
        {
            if (ambiguous.Contains(name)) return;
            if (structs.ContainsKey(name) || typedefs.ContainsKey(name))
            {
                structs.Remove(name);
                typedefs.Remove(name);
                ambiguous.Add(name);
                return;
            }
            define();
        }

        // Pass 1: FILE-SCOPE typedefs and struct definitions, in source order (a typedef is
        // visible after it). Anything inside braces (a function body, a technique) is skipped: a
        // local struct or typedef there is the function's own and must not replace a file-scope one.
        int depth = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Token tok = code[i];
            if (tok.Kind == TokenKind.LBrace) { depth++; continue; }
            if (tok.Kind == TokenKind.RBrace) { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0 || tok.Kind != TokenKind.Identifier) continue;
            if (tok.Text == "typedef")
            {
                int k = ReadModifiers(i + 1, code.Count, out bool? major, out _, out _);
                if (TryType(k, out TypeRef t, out int after) && after < code.Count && code[after].Kind == TokenKind.Identifier)
                {
                    string alias = code[after].Text;
                    if (t.IsMatrix)
                        Define(alias, () => typedefs[alias] = t with { RowMajor = major ?? t.RowMajor });
                    else if (structs.TryGetValue(t.Name, out StructDef? aliased))
                        Define(alias, () => structs[alias] = aliased);
                    else if (ambiguous.Contains(t.Name))
                        ambiguous.Add(alias);
                }
                continue;
            }
            if (tok.Text == "struct" && i + 2 < code.Count && code[i + 1].Kind == TokenKind.Identifier
                && code[i + 2].Kind == TokenKind.LBrace)
            {
                int close = MatchClose(i + 2, TokenKind.LBrace, TokenKind.RBrace);
                if (close < 0) break;
                string name = code[i + 1].Text;
                var members = ReadDeclarations(i + 3, close, members: true);
                Define(name, () => structs[name] = new StructDef(members));
                i = close;
            }
        }

        // Pass 2: function definitions at file scope: [modifiers] TYPE NAME ( params ) [SEM] {
        int braceDepth = 0;
        for (int i = 0; i < code.Count; i++)
        {
            Token t = code[i];
            if (t.Kind == TokenKind.LBrace) { braceDepth++; continue; }
            if (t.Kind == TokenKind.RBrace) { braceDepth = Math.Max(0, braceDepth - 1); continue; }
            if (braceDepth != 0 || t.Kind != TokenKind.Identifier || i + 1 >= code.Count
                || code[i + 1].Kind != TokenKind.LParen || i == 0)
            {
                continue;
            }
            int close = MatchClose(i + 1, TokenKind.LParen, TokenKind.RParen);
            if (close < 0) break;
            int after = close + 1;
            string? retSemantic = null;
            if (after < code.Count && code[after].Kind == TokenKind.Identifier)
            {
                retSemantic = code[after].Text;
                after++;
            }
            if (after >= code.Count || code[after].Kind != TokenKind.LBrace)
                continue;

            // The return type ends right before the name: an identifier, or matrix<...>.
            int typeEnd = i - 1;
            int typeStart = typeEnd;
            if (code[typeEnd].Kind == TokenKind.RAngle)
            {
                while (typeStart > 0 && code[typeStart].Kind != TokenKind.LAngle) typeStart--;
                typeStart--;
            }
            if (typeStart < 0 || code[typeStart].Kind != TokenKind.Identifier) continue;
            int modStart = typeStart;
            while (modStart > 0 && code[modStart - 1].Kind == TokenKind.Identifier && Modifiers.Contains(code[modStart - 1].Text))
                modStart--;
            ReadModifiers(modStart, typeStart, out bool? retMajor, out _, out _);
            TryType(typeStart, out TypeRef ret, out _);

            var parameters = ReadDeclarations(i + 2, close, members: false);
            if (!functions.TryGetValue(t.Text, out var list))
                functions[t.Text] = list = [];
            list.Add((ret, retMajor, retSemantic, parameters));
            i = close;
        }

        var memo = new Dictionary<string, Flattened>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);

        var result = new Dictionary<string, EntryPointMatrixInterpolants>(StringComparer.Ordinal);
        foreach ((string name, var definitions) in functions)
        {
            var inputs = new List<MatrixInterpolantDeclaration>();
            var outputs = new List<MatrixInterpolantDeclaration>();
            var unmodelled = new List<string>();
            if (definitions.Count > 1)
                unmodelled.Add($"'{name}' is defined {definitions.Count} times (overloads), so which signature an entry point uses is not modelled");
            foreach (var (ret, retMajor, retSemantic, parameters) in definitions)
            {
                AddTop(new Declarator(ret, name, retSemantic, retMajor, "out"), outputs, inputs, unmodelled);
                foreach (Declarator p in parameters)
                    AddTop(p, outputs, inputs, unmodelled);
            }
            result[name] = new EntryPointMatrixInterpolants(inputs, outputs, unmodelled);
        }
        return result;

        // Declarations between from (inclusive) and to (exclusive): struct members, or parameters.
        List<Declarator> ReadDeclarations(int from, int to, bool members)
        {
            var found = new List<Declarator>();
            int k = from;
            while (k < to)
            {
                int typeAt = ReadModifiers(k, to, out bool? major, out string direction, out bool uniform);
                if (!TryType(typeAt, out TypeRef type, out int d) || d >= to)
                {
                    k = SkipTo(k, to, members);
                    continue;
                }
                while (true)
                {
                    if (d >= to || code[d].Kind != TokenKind.Identifier) { d = SkipTo(d, to, members); break; }
                    string name = code[d].Text;
                    d++;
                    while (d < to && code[d].Kind == TokenKind.Number) d++;          // NAME[count]
                    string? semantic = null;
                    if (d < to && code[d].Kind == TokenKind.Identifier)
                    {
                        semantic = code[d].Text;
                        d++;
                    }
                    if (!uniform)
                        found.Add(new Declarator(type, name, semantic, major, direction));
                    // A parameter's default value, or anything else before the separator.
                    while (d < to && code[d].Kind is not (TokenKind.Comma or TokenKind.Semicolon)) d++;
                    if (members && d < to && code[d].Kind == TokenKind.Comma)
                    {
                        d++;                                                           // A : S0, B : S1;
                        continue;
                    }
                    if (d < to) d++;                                                   // ',' between params, or ';'
                    break;
                }
                k = d;
            }
            return found;
        }

        int SkipTo(int k, int to, bool members)
        {
            TokenKind stop = members ? TokenKind.Semicolon : TokenKind.Comma;
            while (k < to && code[k].Kind != stop) k++;
            return k + 1;
        }

        void AddTop(Declarator decl, List<MatrixInterpolantDeclaration> outputs, List<MatrixInterpolantDeclaration> inputs,
                    List<string> unmodelled)
        {
            var matrices = new List<MatrixInterpolantDeclaration>();
            Collect(decl, matrices, unmodelled, 0);
            if (decl.Direction is "out" or "inout") outputs.AddRange(matrices);
            if (decl.Direction is "in" or "inout") inputs.AddRange(matrices);
        }

        // A declaration's matrix declarations with semantics, flattened through structs.
        void Collect(Declarator decl, List<MatrixInterpolantDeclaration> into, List<string> unmodelled, int level)
        {
            if (decl.Type.IsMatrix)
            {
                if (decl.Semantic is null) return;
                into.Add(new MatrixInterpolantDeclaration(SemanticName.Normalize(decl.Semantic), decl.Name,
                    decl.Type.Rows, decl.Type.Cols, decl.RowMajor ?? decl.Type.RowMajor ?? false));
                return;
            }
            if (ambiguous.Contains(decl.Type.Name))
            {
                unmodelled.Add($"the type '{decl.Type.Name}' of '{decl.Name}' is defined more than once at file scope");
                return;
            }
            if (!structs.ContainsKey(decl.Type.Name)) return;
            Flattened f = Flatten(decl.Type.Name, level);
            if (decl.Semantic is not null)
            {
                if (f.HasMatrix)
                {
                    unmodelled.Add($"'{decl.Name}' carries the semantic {decl.Semantic} on a struct type ('{decl.Type.Name}') " +
                                   "that holds a matrix, so the matrix's registers come from the outer semantic");
                }
                return;
            }
            into.AddRange(f.Matrices);
            unmodelled.AddRange(f.Unmodelled);
        }

        Flattened Flatten(string structName, int level)
        {
            if (memo.TryGetValue(structName, out Flattened? done)) return done;
            if (level > MaxDepth || !inProgress.Add(structName))
                return new Flattened([], [$"struct '{structName}' nests deeper than {MaxDepth} levels or contains itself"], false);
            var matrices = new List<MatrixInterpolantDeclaration>();
            var unmodelled = new List<string>();
            bool hasMatrix = false;
            foreach (Declarator member in structs[structName].Members)
            {
                if (member.Type.IsMatrix)
                    hasMatrix = true;
                else if (structs.ContainsKey(member.Type.Name) && Flatten(member.Type.Name, level + 1).HasMatrix)
                    hasMatrix = true;
                Collect(member, matrices, unmodelled, level + 1);
                if (matrices.Count > MaxFlattened)
                {
                    matrices.Clear();
                    unmodelled.Add($"struct '{structName}' flattens to more than {MaxFlattened} matrix declarations");
                    break;
                }
            }
            inProgress.Remove(structName);
            var flattened = new Flattened(matrices, unmodelled.Distinct().ToList(), hasMatrix);
            memo[structName] = flattened;
            return flattened;
        }
    }
}
