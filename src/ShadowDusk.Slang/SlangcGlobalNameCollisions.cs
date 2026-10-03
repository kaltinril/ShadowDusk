#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;

namespace ShadowDusk.Slang;

/// <summary>
/// Issue #323: finds two global declarations of one name that slangc's <c>-no-mangle</c>
/// output cannot keep apart, so the compile fails by name instead of handing a silently merged
/// program downstream, or crashing slangc.
/// </summary>
/// <remarks>
/// <para>ShadowDusk compiles Slang with <c>-no-mangle</c> (Phase 66 A4) so that every parameter
/// reaches the effect under the author's spelling. slangc v2026.14.1 then drops the namespace
/// from a global's name and does NOT keep two globals of one simple name apart (measured):</para>
/// <list type="bullet">
/// <item><c>namespace A { Texture2D T; } namespace B { Texture2D T; }</c>, with both sampled,
/// is emitted as ONE <c>Texture2D&lt;float4 &gt; T</c> that both reads use. The same for a
/// namespaced global beside a plain one of the same name, for nested and dotted namespaces
/// (<c>namespace A.X</c>, <c>B::Y</c>), for two combined <c>Sampler2D Comb</c> (one
/// <c>Comb_texture_0</c>), for two <c>SamplerState S</c>, for two struct-typed globals (one
/// <c>gM_t_0</c>), for two <c>ParameterBlock&lt;P&gt; Blk</c> (one <c>cbuffer Blk</c>), for
/// two <c>cbuffer C</c> blocks (one <c>cbuffer C</c>), and for two <c>static</c> or
/// <c>static const</c> globals (one definition, the first initializer).</item>
/// <item>Two same-named implicit or <c>uniform</c> constant-buffer members
/// (<c>namespace A { float4 Tint; } namespace B { float4 Tint; }</c>) crash slangc outright
/// (exit <c>0xC0000005</c>, empty stderr), which the route could only report as an
/// <c>SD0622</c> with no text.</item>
/// <item>The same across files: a module's <c>namespace A { public Texture2D T; }</c> and the
/// entry's <c>namespace B { Texture2D T; }</c> merge, and so do two modules that each declare
/// a plain <c>public Texture2D T</c> used only inside themselves. The entry's own plain global
/// beside a module's of the same name is slangc's own error (<c>E39999</c> ambiguous
/// reference) whenever the entry refers to the name.</item>
/// </list>
/// <para>Same-named FUNCTIONS and TYPES in different namespaces are fine: slangc prefixes those
/// with the namespace (<c>A_f_0</c>, <c>B_f_0</c>), and they are not effect parameters.</para>
/// <para><b>What is read.</b> The entry source's own text first, before slangc runs, so the
/// crashing shape never reaches slangc: the raw text when it holds no preprocessor directive
/// and no <c>-D</c> value can rewrite it (then it is token for token what slangc compiles,
/// since <c>-E</c> expands neither <c>import</c> nor <c>__include</c>), else slangc's own
/// preprocess-only output, run only when the raw text already shows a candidate pair (so no
/// source without one pays a slangc run; a pair that lives in mutually exclusive <c>#if</c>
/// branches is cleared by that run). After the compile, every text the register pass read
/// (issue #292: the entry source and the modules reached by quoted-path import) is checked
/// together, which is where a macro-formed pair and a cross-module pair are found at no extra
/// cost. What that leaves undetected is recorded in <c>docs/validation-matrix.md</c>: a pair
/// formed by macros in a source whose raw text shows none, when no register pass ran; and a
/// pair across modules the register pass did not read. A crash with no text still fails loudly
/// as <c>SD0622</c>, naming the exit code and this shape as its known trigger.</para>
/// </remarks>
internal static class SlangcGlobalNameCollisions
{
    /// <summary><c>SD0643</c>: two global declarations of one name slangc's <c>-no-mangle</c> output merges.</summary>
    public const string Code = "SD0643";

    /// <summary>What a global declaration is: a variable (shader parameter or static), or the
    /// name of a constant-buffer block (a parameter group).</summary>
    public enum DeclarationKind
    {
        Variable,
        ParameterGroup,
    }

    /// <summary>One global-scope declaration found in a text.</summary>
    /// <param name="Name">The simple (unqualified) name.</param>
    /// <param name="Namespace">The namespace path with <c>.</c> separators, empty at the global scope.</param>
    /// <param name="File">The file the text belongs to.</param>
    /// <param name="Line">1-based line, or 0 when the text carries no line structure (a <c>-E</c> token stream).</param>
    /// <param name="Column">1-based column, or 0 likewise.</param>
    /// <param name="Kind">Variable or parameter-group name.</param>
    public readonly record struct GlobalDeclaration(
        string Name, string Namespace, string File, int Line, int Column, DeclarationKind Kind)
    {
        /// <summary><c>A.T</c>, or <c>T</c> at the global scope.</summary>
        public string QualifiedName => Namespace.Length == 0 ? Name : Namespace + "." + Name;
    }

    /// <summary>Two declarations slangc's output cannot keep apart.</summary>
    public sealed record Collision(GlobalDeclaration First, GlobalDeclaration Second);

    /// <summary>
    /// The source (or a <c>-D</c> value) can spell <c>namespace</c>, the only way two globals of
    /// one name can both be legal Slang in one text. False means nothing to look for; a source
    /// with no namespace that declares one name twice is slangc's own <c>E30200</c>.
    /// </summary>
    public static bool MaySpellNamespace(string slangSource, IReadOnlyList<UserDefine> defines)
    {
        if (slangSource.Contains("namespace", StringComparison.Ordinal))
            return true;
        foreach (UserDefine define in defines)
        {
            if (define.Name.Contains("namespace", StringComparison.Ordinal)
                || (define.Value?.Contains("namespace", StringComparison.Ordinal) ?? false))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The raw entry text is token for token what slangc compiles from it: no preprocessor
    /// directive, no line splice, and no <c>-D</c> name it spells (a define could rename a
    /// declaration or form one). <c>import</c> and <c>__include</c> do not matter here: slangc's
    /// own <c>-E</c> expands neither, so the entry text is the same either way.
    /// </summary>
    public static bool RawTextIsWhatSlangcCompiles(string slangSource, IReadOnlyList<UserDefine> defines)
    {
        if (slangSource.Contains('#') || slangSource.Contains('\\'))
            return false;
        foreach (UserDefine define in defines)
        {
            if (Regex.IsMatch(slangSource, $@"(?<!\w){Regex.Escape(define.Name)}(?!\w)"))
                return false;
            if (define.Value is not null && define.Value.Contains("namespace", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Every global-scope variable and constant-buffer-block declaration in <paramref name="text"/>,
    /// with the namespace it is in.
    /// </summary>
    /// <param name="text">Slang source as the author wrote it (<paramref name="rawSource"/> true:
    /// comments are masked and preprocessor lines skipped, lines and columns are recorded) or
    /// slangc's preprocess-only output (false: one token stream, no line information).</param>
    /// <param name="file">The file the text belongs to, for the declarations' location.</param>
    /// <param name="rawSource">See <paramref name="text"/>.</param>
    public static IReadOnlyList<GlobalDeclaration> Scan(string text, string file, bool rawSource)
    {
        string masked = SlangSourceMask.Mask(text);
        if (rawSource)
            masked = BlankDirectiveLines(masked);
        return new Parser(masked, file, rawSource).Parse();
    }

    /// <summary>
    /// The pairs slangc merges: two declarations of one simple name in different namespaces
    /// (including the global scope), or in two different files that are both modules (never the
    /// entry source beside a module at one path, which slangc reports itself as an ambiguous
    /// reference when it matters). Each name is reported once, by its first two declarations.
    /// </summary>
    /// <param name="declarations">From one or more texts.</param>
    /// <param name="entryFile">The <see cref="GlobalDeclaration.File"/> of the entry source's declarations.</param>
    public static IReadOnlyList<Collision> FindCollisions(IEnumerable<GlobalDeclaration> declarations, string entryFile)
    {
        var collisions = new List<Collision>();
        foreach (IGrouping<(string Name, DeclarationKind Kind), GlobalDeclaration> group in
                 declarations.GroupBy(d => (d.Name, d.Kind)))
        {
            GlobalDeclaration[] all = group.ToArray();
            Collision? found = null;
            for (int i = 0; i < all.Length && found is null; i++)
            {
                for (int j = i + 1; j < all.Length; j++)
                {
                    if (Merges(all[i], all[j], entryFile))
                    {
                        found = new Collision(all[i], all[j]);
                        break;
                    }
                }
            }
            if (found is not null)
                collisions.Add(found);
        }
        return collisions;
    }

    private static bool Merges(GlobalDeclaration a, GlobalDeclaration b, string entryFile)
    {
        if (!string.Equals(a.Namespace, b.Namespace, StringComparison.Ordinal))
            return true;
        bool differentFiles = !string.Equals(
            SlangcRegisterStripper.PathKey(a.File), SlangcRegisterStripper.PathKey(b.File), StringComparison.Ordinal);
        return differentFiles && a.File != entryFile && b.File != entryFile;
    }

    /// <summary>The <c>SD0643</c> for <paramref name="collision"/>, located at the second
    /// declaration when its text had lines, else at the first, else at the entry source.</summary>
    public static ShaderError Error(Collision collision, string sourceName)
    {
        GlobalDeclaration at = collision.Second.Line > 0 ? collision.Second
            : collision.First.Line > 0 ? collision.First
            : collision.Second;
        string what = collision.First.Kind == DeclarationKind.ParameterGroup ? "constant buffer" : "global";
        return new ShaderError(
            File: at.Line > 0 ? at.File : sourceName,
            Line: at.Line,
            Column: at.Column,
            Code: Code,
            Message: $"The {what} '{collision.First.Name}' is declared twice in places slangc's output cannot keep apart: " +
                     $"'{collision.First.QualifiedName}' ({Where(collision.First)}) and " +
                     $"'{collision.Second.QualifiedName}' ({Where(collision.Second)}). ShadowDusk compiles Slang with " +
                     "slangc's -no-mangle so that parameter names reach the effect as written, and slangc (v" +
                     $"{SlangToolPath.SlangVersion}) then emits ONE declaration named '{collision.First.Name}' for both " +
                     "(measured): the shader silently reads one resource or value where two were declared, and two " +
                     "same-named constant-buffer members crash slangc outright. Give every global variable, shader " +
                     "parameter and constant buffer a name that is unique across namespaces and imported modules.");
    }

    private static string Where(GlobalDeclaration d) =>
        d.Line > 0 ? $"{d.File}:{d.Line}:{d.Column}" : d.File;

    /// <summary>
    /// Gives declarations found in a text with no line structure (slangc's <c>-E</c> output of the
    /// entry source) the line and column of the same declaration in the raw entry source, when
    /// the raw text shows one under the same namespace and name.
    /// </summary>
    public static Collision Locate(Collision collision, IReadOnlyList<GlobalDeclaration> rawEntryDeclarations) =>
        new(Locate(collision.First, rawEntryDeclarations), Locate(collision.Second, rawEntryDeclarations));

    private static GlobalDeclaration Locate(GlobalDeclaration d, IReadOnlyList<GlobalDeclaration> raw)
    {
        if (d.Line > 0)
            return d;
        foreach (GlobalDeclaration candidate in raw)
        {
            if (candidate.Kind == d.Kind && candidate.Name == d.Name && candidate.Namespace == d.Namespace
                && candidate.File == d.File)
            {
                return candidate;
            }
        }
        return d;
    }

    // A line whose first non-blank character is '#': blanked (its newline kept), so a directive's
    // tokens are never read as declarations. The raw text is only trusted when it has none; this
    // keeps the candidate scan sane when it has some.
    private static string BlankDirectiveLines(string masked)
    {
        string[] lines = masked.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int first = 0;
            while (first < line.Length && (line[first] == ' ' || line[first] == '\t' || line[first] == '\r'))
                first++;
            if (first < line.Length && line[first] == '#')
                lines[i] = new string(' ', line.Length);
        }
        return string.Join('\n', lines);
    }

    private static readonly Regex TokenPattern = new(
        """[A-Za-z_]\w*|\d[\w.]*|::|[{}()\[\];,:<>=.#]|\S""", RegexOptions.Compiled);

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "static", "const", "uniform", "extern", "export", "__extern",
        "groupshared", "shared", "row_major", "column_major", "precise", "nointerpolation", "linear",
        "centroid", "sample", "noperspective", "in", "out", "inout", "volatile", "__target_intrinsic",
        "__builtin", "__exported", "override", "mutating", "unsafe", "dynamic_uniform", "no_diff",
    };

    // Statements that declare no global variable: skipped to their ';' or their closing brace.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "struct", "class", "interface", "enum", "extension", "typedef", "typealias", "import", "__include",
        "module", "implementing", "using", "__generic", "property", "associatedtype", "__init", "__subscript",
        "syntax", "__intrinsic_type", "__magic_type", "return",
    };

    private sealed class Parser
    {
        private readonly string _file;
        private readonly bool _rawSource;
        private readonly List<(string Text, int Index)> _tokens = [];
        private readonly int[] _lineStarts;
        private readonly List<GlobalDeclaration> _found = [];
        private readonly Stack<(bool Body, string Namespace)> _scopes = new();
        private int _i;

        public Parser(string masked, string file, bool rawSource)
        {
            _file = file;
            _rawSource = rawSource;
            foreach (Match m in TokenPattern.Matches(masked))
                _tokens.Add((m.Value, m.Index));
            var starts = new List<int> { 0 };
            for (int i = 0; i < masked.Length; i++)
            {
                if (masked[i] == '\n')
                    starts.Add(i + 1);
            }
            _lineStarts = starts.ToArray();
        }

        private string Namespace => _scopes.Count == 0 ? "" : _scopes.Peek().Namespace;

        private bool InBody => _scopes.Any(s => s.Body);

        private string? Peek(int offset = 0) =>
            _i + offset < _tokens.Count ? _tokens[_i + offset].Text : null;

        private static bool IsIdentifier(string? token) =>
            token is not null && (char.IsLetter(token[0]) || token[0] == '_');

        public IReadOnlyList<GlobalDeclaration> Parse()
        {
            while (_i < _tokens.Count)
            {
                string token = _tokens[_i].Text;
                if (token == "}")
                {
                    if (_scopes.Count > 0)
                        _scopes.Pop();
                    _i++;
                    continue;
                }
                if (token == ";")
                {
                    _i++;
                    continue;
                }
                if (InBody)
                {
                    // A function body or a type body: only its braces matter.
                    if (token == "{")
                        _scopes.Push((true, Namespace));
                    _i++;
                    continue;
                }
                if (token == "[")
                {
                    SkipBalanced("[", "]");
                    continue;
                }
                if (token == "{")
                {
                    _scopes.Push((true, Namespace));
                    _i++;
                    continue;
                }
                if (token == "namespace")
                {
                    ParseNamespace();
                    continue;
                }
                if (token is "cbuffer" or "tbuffer")
                {
                    ParseConstantBuffer();
                    continue;
                }
                if (Skipped.Contains(token))
                {
                    SkipStatement();
                    continue;
                }
                ParseDeclaration();
            }
            return _found;
        }

        private void ParseNamespace()
        {
            _i++; // 'namespace'
            var parts = new List<string>();
            while (IsIdentifier(Peek()))
            {
                parts.Add(_tokens[_i].Text);
                _i++;
                if (Peek() is "." or "::")
                    _i++;
                else
                    break;
            }
            if (Peek() != "{")
            {
                SkipStatement();
                return;
            }
            _i++; // '{'
            string path = string.Join('.', parts);
            string outer = Namespace;
            _scopes.Push((false, outer.Length == 0 ? path : path.Length == 0 ? outer : outer + "." + path));
        }

        private void ParseConstantBuffer()
        {
            _i++; // 'cbuffer' / 'tbuffer'
            if (IsIdentifier(Peek()))
            {
                Record(_tokens[_i], DeclarationKind.ParameterGroup);
                _i++;
            }
            // ': register(b0)' and the like, up to the block.
            while (_i < _tokens.Count && Peek() is not ("{" or ";"))
                _i++;
            if (Peek() == "{")
            {
                _i++;
                // Its members are globals of the enclosing namespace.
                _scopes.Push((false, Namespace));
            }
        }

        private void ParseDeclaration()
        {
            while (Modifiers.Contains(Peek() ?? ""))
                _i++;
            if (Peek() == "[")
            {
                SkipBalanced("[", "]");
                while (Modifiers.Contains(Peek() ?? ""))
                    _i++;
            }
            if (!IsIdentifier(Peek()) || Skipped.Contains(Peek()!) || Peek() is "namespace" or "cbuffer" or "tbuffer")
            {
                if (IsIdentifier(Peek()) && (Skipped.Contains(Peek()!) || Peek() is "namespace" or "cbuffer" or "tbuffer"))
                    return; // Let the main loop dispatch the keyword.
                SkipStatement();
                return;
            }

            // The type: 'float4', 'Texture2D<float4>', 'A.B.T', 'ParameterBlock<P>'.
            _i++;
            while (true)
            {
                if (Peek() is "." or "::" && IsIdentifier(Peek(1)))
                {
                    _i += 2;
                    continue;
                }
                if (Peek() == "<")
                {
                    SkipBalanced("<", ">");
                    continue;
                }
                break;
            }

            if (!IsIdentifier(Peek()))
            {
                SkipStatement();
                return;
            }
            (string Text, int Index) name = _tokens[_i];
            _i++;

            if (Peek() == "<")
                SkipBalanced("<", ">"); // generic parameters of a function
            if (Peek() == "(")
            {
                // A function: parameters, then a prototype's ';' or a body (after an optional
                // return semantic or 'where' clause).
                SkipBalanced("(", ")");
                while (_i < _tokens.Count && Peek() is not ("{" or ";"))
                    _i++;
                if (Peek() == "{")
                    SkipBalanced("{", "}");
                else if (Peek() == ";")
                    _i++;
                return;
            }
            if (Peek() is not (";" or ":" or "=" or "[" or ","))
            {
                SkipStatement();
                return;
            }

            // A variable, possibly with more declarators: 'Texture2D A, B;'.
            Record(name, DeclarationKind.Variable);
            while (_i < _tokens.Count)
            {
                string t = _tokens[_i].Text;
                if (t == ";")
                {
                    _i++;
                    return;
                }
                if (t == ",")
                {
                    _i++;
                    if (IsIdentifier(Peek()))
                    {
                        Record(_tokens[_i], DeclarationKind.Variable);
                        _i++;
                    }
                    continue;
                }
                if (t is "(" or "[" or "{")
                {
                    SkipBalanced(t, t == "(" ? ")" : t == "[" ? "]" : "}");
                    continue;
                }
                _i++;
            }
        }

        private void Record((string Text, int Index) token, DeclarationKind kind)
        {
            int line = 0;
            int column = 0;
            if (_rawSource)
            {
                int at = Array.BinarySearch(_lineStarts, token.Index);
                if (at < 0)
                    at = ~at - 1;
                line = at + 1;
                column = token.Index - _lineStarts[at] + 1;
            }
            _found.Add(new GlobalDeclaration(token.Text, Namespace, _file, line, column, kind));
        }

        // Skips to the end of the current statement: past its ';', or past a brace block that
        // comes first (a struct, an extension, a function body) and its optional ';'.
        private void SkipStatement()
        {
            while (_i < _tokens.Count)
            {
                string t = _tokens[_i].Text;
                if (t == ";")
                {
                    _i++;
                    return;
                }
                if (t == "{")
                {
                    SkipBalanced("{", "}");
                    if (Peek() == ";")
                        _i++;
                    return;
                }
                if (t == "}")
                    return; // The enclosing scope closes: let the main loop pop it.
                if (t is "(" or "[")
                {
                    SkipBalanced(t, t == "(" ? ")" : "]");
                    continue;
                }
                _i++;
            }
        }

        private void SkipBalanced(string open, string close)
        {
            int depth = 0;
            while (_i < _tokens.Count)
            {
                string t = _tokens[_i].Text;
                _i++;
                if (t == open)
                    depth++;
                else if (t == close && --depth == 0)
                    return;
                else if (open == "<" && t is ";" or "{" or "}")
                    return; // Not a generic argument list after all ('a < b;').
            }
        }
    }
}
