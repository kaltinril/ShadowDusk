#nullable enable

using System.Text;
using System.Text.RegularExpressions;

namespace ShadowDusk.GLSL;

/// <summary>
/// Rule 17 (issue #453, MonoGame 3.8.6 DesktopGL4): lowers every non-square matrix
/// (<c>mat2x3</c> … <c>mat4x3</c>) in the rewritten GLSL to a zero-padded square <c>matN</c>,
/// N = max(columns, rows), together with the matrix-from-matrix constructors that come with
/// them. Both are GLSL 1.20 / ESSL 3.00 constructs: the versionless dialect the rewriter emits
/// (GLSL 1.10 / ESSL 1.00) cannot declare them, so strict front ends (Mesa, macOS, WebGL) and
/// MonoGame 3.8.6's DesktopGL4 core-profile runtime refuse the shader at Effect load (DesktopGL4
/// aborts the process). <c>mgfxc</c>'s output never contains them.
/// <para>
/// They reach the output from two places: SPIRV-Cross's truncation locals (DXC turns
/// <c>mul(float3, float4x4)</c> or a <c>(float3x3)</c> cast into a <c>mat3x4</c> temporary) and
/// the rewriter's own non-square matrix-interpolant temporary / pixel-stage reconstruction
/// (Rule 4c). The lowering is the same for both.
/// </para>
/// <para>
/// <b>The representation.</b> A logical <c>matCxR</c> is held as a physical <c>matN</c> whose
/// column <c>c &lt; C</c> is the logical column padded with zero rows, and whose columns
/// <c>c ≥ C</c> are zero. Every padding element is exactly zero, so the logical values never
/// mix with the padding: <c>M * v</c> becomes <c>(M * vecN(v, 0.0)).xyz</c>, <c>v * M</c> becomes
/// <c>(vecN(v, 0.0) * M).xy</c>, <c>M[i]</c> becomes <c>M[i].xyz</c>, and a product of two padded
/// matrices of one size stays padded. Padding values are exact zeros added to the logical sums,
/// so the results are bit-identical to the non-square arithmetic.
/// </para>
/// <para>
/// <b>Typed, all-or-nothing.</b> Each statement that touches a non-square matrix is parsed and
/// typed (declared types, builtin return types, swizzles); a statement shape the pass cannot type
/// or does not model makes the WHOLE pass decline, and the text is returned unchanged, so the
/// SD0403 portability warning (which looks for a surviving non-square type) still fires. A
/// partial lowering would leave a <c>matN</c> declaration beside an unlowered use, which no
/// driver accepts. A shader with no non-square type is returned byte-identical without parsing.
/// </para>
/// </summary>
internal static class NonSquareMatrixLowering
{
    private static readonly Regex NonSquareOrXSpelled = new(@"\bmat[234]x[234]\b", RegexOptions.Compiled);

    /// <summary>
    /// Lowers the non-square matrices of <paramref name="body"/>, or returns it unchanged when it
    /// has none or when any statement touching one is a shape the pass does not model.
    /// </summary>
    public static string Lower(string body)
    {
        if (!NonSquareOrXSpelled.IsMatch(body))
        {
            return body;
        }
        return new Lowerer(body).Run() ?? body;
    }

    // ---------------------------------------------------------------- types

    /// <summary>
    /// A GLSL type. <c>Cols == 0</c> for scalars and vectors (<c>Rows</c> = component count, 1 for a
    /// scalar); <c>Cols &gt; 0</c> for a matrix. <c>Element</c> is set for an array.
    /// </summary>
    private sealed record GType(char Base, int Cols, int Rows, GType? Element = null)
    {
        public static readonly GType Float = new('f', 0, 1);
        public static readonly GType Int = new('i', 0, 1);
        public static readonly GType Bool = new('b', 0, 1);
        public static readonly GType Vec4 = new('f', 0, 4);
        public static readonly GType Other = new('s', 0, 1);

        public bool IsArray => Element is not null;
        public bool IsScalar => !IsArray && Base != 's' && Cols == 0 && Rows == 1;
        public bool IsVector => !IsArray && Cols == 0 && Rows > 1;
        public bool IsMatrix => !IsArray && Cols > 0;
        public bool IsNonSquare => IsMatrix && Cols != Rows;

        /// <summary>The square size a matrix is held at.</summary>
        public int Phys => Math.Max(Cols, Rows);

        public static GType Vec(char b, int n) => n == 1 ? new GType(b, 0, 1) : new GType(b, 0, n);
    }

    private static GType? ParseTypeName(string s)
    {
        switch (s)
        {
            case "float": return GType.Float;
            case "int": return GType.Int;
            case "uint": return new GType('u', 0, 1);
            case "bool": return GType.Bool;
        }
        var m = Regex.Match(s, @"^(vec|ivec|uvec|bvec)([234])$");
        if (m.Success)
        {
            char b = m.Groups[1].Value[0] == 'v' ? 'f' : m.Groups[1].Value[0];
            return GType.Vec(b, m.Groups[2].Value[0] - '0');
        }
        m = Regex.Match(s, @"^mat([234])(?:x([234]))?$");
        if (m.Success)
        {
            int c = m.Groups[1].Value[0] - '0';
            int r = m.Groups[2].Success ? m.Groups[2].Value[0] - '0' : c;
            return new GType('f', c, r);
        }
        if (s.StartsWith("sampler", StringComparison.Ordinal))
        {
            return GType.Other;
        }
        return null;
    }

    private static bool IsXSpelledMatrix(string s) => Regex.IsMatch(s, @"^mat[234]x[234]$");

    /// <summary>The physical type name a matrix type is emitted as (<c>matN</c>).</summary>
    private static string PhysName(GType t) => $"mat{t.Phys}";

    private const string Swz = "xyzw";

    // ---------------------------------------------------------------- tokens

    private enum TokKind { Ident, Number, Punct, Preproc }

    private readonly record struct Tok(TokKind Kind, string Text, int Start, int End);

    private static readonly string[] Puncts =
    {
        "<<=", ">>=", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        "==", "!=", "<=", ">=", "&&", "||", "^^", "<<", ">>",
    };

    private static List<Tok>? Tokenize(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        bool lineStart = true;
        while (i < s.Length)
        {
            char ch = s[i];
            if (ch == '\n')
            {
                lineStart = true;
                i++;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (ch == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) return null;
                i = close + 2;
                continue;
            }
            if (ch == '#' && lineStart)
            {
                int start = i;
                while (i < s.Length && s[i] != '\n') i++;
                toks.Add(new Tok(TokKind.Preproc, s[start..i], start, i));
                continue;
            }
            lineStart = false;
            if (char.IsAsciiLetter(ch) || ch == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(new Tok(TokKind.Ident, s[start..i], start, i));
                continue;
            }
            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                int start = i;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_')
                    {
                        i++;
                    }
                    else if ((c == '+' || c == '-') && (s[i - 1] == 'e' || s[i - 1] == 'E')
                             && !s[start..i].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }
                toks.Add(new Tok(TokKind.Number, s[start..i], start, i));
                continue;
            }
            string? p = Puncts.FirstOrDefault(x => string.CompareOrdinal(s, i, x, 0, x.Length) == 0);
            p ??= ch.ToString();
            toks.Add(new Tok(TokKind.Punct, p, i, i + p.Length));
            i += p.Length;
        }
        return toks;
    }

    // ---------------------------------------------------------------- AST

    private abstract class Node
    {
        protected Node(int start, int end) { Start = start; End = end; }
        public int Start { get; }
        public int End { get; }
    }

    private sealed class IdentNode(string name, int s, int e) : Node(s, e) { public string Name { get; } = name; }
    private sealed class LiteralNode(GType type, int s, int e) : Node(s, e) { public GType Type { get; } = type; }
    private sealed class ParenNode(Node inner, int s, int e) : Node(s, e) { public Node Inner { get; } = inner; }

    private sealed class CallNode(Tok callee, List<Node> args, int s, int e) : Node(s, e)
    {
        public Tok Callee { get; } = callee;
        public List<Node> Args { get; } = args;
    }

    private sealed class IndexNode(Node b, Node idx, int s, int e) : Node(s, e)
    {
        public Node Base { get; } = b;
        public Node Idx { get; } = idx;
    }

    private sealed class FieldNode(Node b, string name, int s, int e) : Node(s, e)
    {
        public Node Base { get; } = b;
        public string Name { get; } = name;
    }

    private sealed class UnaryNode(string op, Node operand, bool postfix, int s, int e) : Node(s, e)
    {
        public string Op { get; } = op;
        public Node Operand { get; } = operand;
        public bool Postfix { get; } = postfix;
    }

    private sealed class BinaryNode(string op, Node l, Node r, int s, int e) : Node(s, e)
    {
        public string Op { get; } = op;
        public Node L { get; } = l;
        public Node R { get; } = r;
    }

    private sealed class TernaryNode(Node c, Node a, Node b, int s, int e) : Node(s, e)
    {
        public Node C { get; } = c;
        public Node A { get; } = a;
        public Node B { get; } = b;
    }

    private sealed class AssignNode(string op, Node l, Node r, int s, int e) : Node(s, e)
    {
        public string Op { get; } = op;
        public Node L { get; } = l;
        public Node R { get; } = r;
    }

    /// <summary>A recursive-descent parser over one statement's tokens. Every method returns null on a shape it does not model.</summary>
    private sealed class Parser(List<Tok> toks, int pos, int end)
    {
        private int _pos = pos;

        public bool AtEnd => _pos >= end;

        private Tok? Peek => _pos < end ? toks[_pos] : null;

        private bool IsPunct(string p) => Peek is { Kind: TokKind.Punct } t && t.Text == p;

        public Node? ParseExpression()
        {
            Node? left = ParseAssignment();
            while (left is not null && IsPunct(","))
            {
                _pos++;
                Node? right = ParseAssignment();
                if (right is null) return null;
                left = new BinaryNode(",", left, right, left.Start, right.End);
            }
            return left;
        }

        private static readonly HashSet<string> AssignOps = new(StringComparer.Ordinal)
        {
            "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=",
        };

        private Node? ParseAssignment()
        {
            Node? left = ParseConditional();
            if (left is null) return null;
            if (Peek is { Kind: TokKind.Punct } t && AssignOps.Contains(t.Text))
            {
                _pos++;
                Node? right = ParseAssignment();
                if (right is null) return null;
                return new AssignNode(t.Text, left, right, left.Start, right.End);
            }
            return left;
        }

        private Node? ParseConditional()
        {
            Node? c = ParseBinary(0);
            if (c is null) return null;
            if (!IsPunct("?")) return c;
            _pos++;
            Node? a = ParseExpression();
            if (a is null || !IsPunct(":")) return null;
            _pos++;
            Node? b = ParseAssignment();
            if (b is null) return null;
            return new TernaryNode(c, a, b, c.Start, b.End);
        }

        private static readonly string[][] Levels =
        {
            new[] { "||" }, new[] { "^^" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" },
            new[] { "==", "!=" }, new[] { "<", ">", "<=", ">=" }, new[] { "<<", ">>" },
            new[] { "+", "-" }, new[] { "*", "/", "%" },
        };

        private Node? ParseBinary(int level)
        {
            if (level == Levels.Length) return ParseUnary();
            Node? left = ParseBinary(level + 1);
            while (left is not null && Peek is { Kind: TokKind.Punct } t && Levels[level].Contains(t.Text))
            {
                _pos++;
                Node? right = ParseBinary(level + 1);
                if (right is null) return null;
                left = new BinaryNode(t.Text, left, right, left.Start, right.End);
            }
            return left;
        }

        private Node? ParseUnary()
        {
            if (Peek is { Kind: TokKind.Punct } t && (t.Text is "-" or "+" or "!" or "~" or "++" or "--"))
            {
                _pos++;
                Node? operand = ParseUnary();
                if (operand is null) return null;
                return new UnaryNode(t.Text, operand, postfix: false, t.Start, operand.End);
            }
            return ParsePostfix();
        }

        private Node? ParsePostfix()
        {
            Node? n = ParsePrimary();
            while (n is not null && Peek is { Kind: TokKind.Punct } t)
            {
                if (t.Text == "[")
                {
                    _pos++;
                    Node? idx = ParseExpression();
                    if (idx is null || !IsPunct("]")) return null;
                    int e = toks[_pos].End;
                    _pos++;
                    n = new IndexNode(n, idx, n.Start, e);
                }
                else if (t.Text == ".")
                {
                    _pos++;
                    if (Peek is not { Kind: TokKind.Ident } f) return null;
                    _pos++;
                    n = new FieldNode(n, f.Text, n.Start, f.End);
                }
                else if (t.Text is "++" or "--")
                {
                    _pos++;
                    n = new UnaryNode(t.Text, n, postfix: true, n.Start, t.End);
                }
                else
                {
                    break;
                }
            }
            return n;
        }

        private Node? ParsePrimary()
        {
            if (Peek is not { } t) return null;
            if (t.Kind == TokKind.Number)
            {
                _pos++;
                string x = t.Text;
                bool isHex = x.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                GType type = !isHex && (x.Contains('.') || x.Contains('e') || x.Contains('E'))
                    ? GType.Float
                    : x.EndsWith('u') || x.EndsWith('U') ? new GType('u', 0, 1) : GType.Int;
                return new LiteralNode(type, t.Start, t.End);
            }
            if (t.Kind == TokKind.Ident)
            {
                _pos++;
                if (t.Text is "true" or "false")
                {
                    return new LiteralNode(GType.Bool, t.Start, t.End);
                }
                if (IsPunct("("))
                {
                    _pos++;
                    var args = new List<Node>();
                    if (IsPunct(")"))
                    {
                        int e0 = toks[_pos].End;
                        _pos++;
                        return new CallNode(t, args, t.Start, e0);
                    }
                    if (Peek is { Kind: TokKind.Ident, Text: "void" } && _pos + 1 < end && toks[_pos + 1].Text == ")")
                    {
                        _pos++;
                        int e0 = toks[_pos].End;
                        _pos++;
                        return new CallNode(t, args, t.Start, e0);
                    }
                    while (true)
                    {
                        Node? a = ParseAssignment();
                        if (a is null) return null;
                        args.Add(a);
                        if (IsPunct(",")) { _pos++; continue; }
                        if (IsPunct(")")) break;
                        return null;
                    }
                    int e = toks[_pos].End;
                    _pos++;
                    return new CallNode(t, args, t.Start, e);
                }
                return new IdentNode(t.Text, t.Start, t.End);
            }
            if (t.Kind == TokKind.Punct && t.Text == "(")
            {
                _pos++;
                Node? inner = ParseExpression();
                if (inner is null || !IsPunct(")")) return null;
                int e = toks[_pos].End;
                _pos++;
                return new ParenNode(inner, t.Start, e);
            }
            return null;
        }
    }

    // ---------------------------------------------------------------- lowering

    /// <summary>One lowered expression: its physical text and its logical (pre-lowering) type.</summary>
    private readonly record struct Lowered(string Text, GType? Type);

    private sealed class Lowerer(string body)
    {
        private List<Tok> _toks = new();
        private readonly Dictionary<string, GType?> _vars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GType?> _functions = new(StringComparer.Ordinal);
        private readonly List<(int Start, int End, string Text)> _edits = new();

        private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
        {
            "const", "uniform", "attribute", "varying", "in", "out", "inout", "invariant",
            "highp", "mediump", "lowp", "flat", "smooth", "noperspective", "centroid",
        };

        private static readonly HashSet<string> InterfaceQualifiers = new(StringComparer.Ordinal)
        {
            "uniform", "attribute", "varying", "in", "out", "inout",
        };

        public string? Run()
        {
            List<Tok>? toks = Tokenize(body);
            if (toks is null) return null;
            _toks = toks;
            if (!CollectSymbols()) return null;

            int segStart = 0;
            int paren = 0;
            for (int i = 0; i < _toks.Count; i++)
            {
                Tok t = _toks[i];
                if (t.Kind == TokKind.Preproc)
                {
                    if (!HandleSegment(segStart, i, terminator: null)) return null;
                    segStart = i + 1;
                    continue;
                }
                if (t.Kind != TokKind.Punct) continue;
                if (t.Text is "(" or "[") paren++;
                else if (t.Text is ")" or "]") paren--;
                else if (paren == 0 && t.Text is ";" or "{" or "}")
                {
                    if (!HandleSegment(segStart, i, t.Text)) return null;
                    segStart = i + 1;
                }
            }
            if (!HandleSegment(segStart, _toks.Count, terminator: null)) return null;

            var sb = new StringBuilder(body);
            foreach (var (start, end, text) in _edits.OrderByDescending(e => e.Start))
            {
                sb.Remove(start, end - start).Insert(start, text);
            }
            string lowered = sb.ToString();
            // Belt and braces: the pass is all-or-nothing, so a surviving non-square type means a
            // shape slipped past the statement classification; decline rather than ship a mix.
            return NonSquareOrXSpelled.IsMatch(lowered) ? null : lowered;
        }

        /// <summary>
        /// Records every declared variable's type and every function's return type. A non-square
        /// type inside a struct body (its member uses could not be typed) declines the pass.
        /// </summary>
        private bool CollectSymbols()
        {
            var braceIsStruct = new Stack<bool>();
            int paren = 0;
            for (int i = 0; i < _toks.Count; i++)
            {
                Tok t = _toks[i];
                if (t.Kind == TokKind.Punct)
                {
                    if (t.Text == "{")
                    {
                        bool isStruct = (i >= 1 && _toks[i - 1].Text == "struct")
                                        || (i >= 2 && _toks[i - 2].Text == "struct");
                        braceIsStruct.Push(isStruct || braceIsStruct.Any(b => b));
                    }
                    else if (t.Text == "}" && braceIsStruct.Count > 0)
                    {
                        braceIsStruct.Pop();
                    }
                    else if (t.Text is "(" or "[") paren++;
                    else if (t.Text is ")" or "]") paren--;
                    continue;
                }
                if (t.Kind != TokKind.Ident) continue;
                GType? type = ParseTypeName(t.Text);
                if (type is null) continue;
                if (IsXSpelledMatrix(t.Text) && braceIsStruct.Any(b => b)) return false;
                if (i + 1 >= _toks.Count || _toks[i + 1].Kind != TokKind.Ident) continue;
                Tok name = _toks[i + 1];
                string next = i + 2 < _toks.Count ? _toks[i + 2].Text : "";
                if (next == "(")
                {
                    if (braceIsStruct.Count == 0 && paren == 0) Declare(_functions, name.Text, type);
                    continue;
                }
                if (next == "[")
                {
                    Declare(_vars, name.Text, type with { Element = type });
                    continue;
                }
                if (next is "=" or ";" or "," or ")")
                {
                    Declare(_vars, name.Text, type);
                }
            }
            // A name declared twice with different types is ambiguous; if either is a non-square
            // matrix the uses cannot be typed.
            return !_vars.Any(kv => kv.Value is null && WasEverMatrixX(kv.Key));
        }

        private readonly HashSet<string> _everX = new(StringComparer.Ordinal);

        private bool WasEverMatrixX(string name) => _everX.Contains(name);

        private void Declare(Dictionary<string, GType?> table, string name, GType type)
        {
            if (type.IsNonSquare || (type.IsArray && type.Element!.IsNonSquare)) _everX.Add(name);
            if (table.TryGetValue(name, out GType? existing))
            {
                if (existing != type) table[name] = null;
                return;
            }
            table[name] = type;
        }

        private static GType? StripArray(GType t) => t.Element is null ? t : t with { Element = null };

        /// <summary>Whether the token range must be lowered: it names a non-square or x-spelled type, or a variable/function of one.</summary>
        private bool MustLower(int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                Tok t = _toks[i];
                if (t.Kind != TokKind.Ident) continue;
                if (IsXSpelledMatrix(t.Text) || _everX.Contains(t.Text)) return true;
                if (_functions.TryGetValue(t.Text, out GType? f) && f is { IsNonSquare: true }) return true;
            }
            return false;
        }

        private bool MayHaveMatrixConstructor(int from, int to)
        {
            for (int i = from; i + 1 < to; i++)
            {
                if (_toks[i].Kind == TokKind.Ident && _toks[i + 1].Text == "("
                    && ParseTypeName(_toks[i].Text) is { IsMatrix: true })
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Lowers one statement segment <c>[from, to)</c>. Returns false to decline the whole pass:
        /// the segment must be lowered and its shape is not modelled.
        /// </summary>
        private bool HandleSegment(int from, int to, string? terminator)
        {
            if (from >= to) return true;
            bool must = MustLower(from, to);
            if (!must && !MayHaveMatrixConstructor(from, to)) return true;

            bool ok = terminator switch
            {
                ";" => LowerSimpleStatement(from, to),
                "{" => LowerControlHeader(from, to),
                _ => false,
            };
            return ok || !must;
        }

        /// <summary><c>if (e)</c> / <c>else if (e)</c> / <c>while (e)</c> ahead of a <c>{</c>.</summary>
        private bool LowerControlHeader(int from, int to)
        {
            int i = from;
            if (_toks[i].Text == "else") i++;
            if (i >= to || _toks[i].Text is not ("if" or "while")) return false;
            i++;
            if (i >= to || _toks[i].Text != "(" || _toks[to - 1].Text != ")") return false;
            return LowerExpressionRange(i + 1, to - 1);
        }

        private bool LowerSimpleStatement(int from, int to)
        {
            int i = from;
            if (_toks[i].Text == "return")
            {
                return i + 1 == to || LowerExpressionRange(i + 1, to);
            }

            // Declaration: qualifiers, a type, a name, an optional [N], an optional initializer.
            var quals = new List<string>();
            while (i < to && Qualifiers.Contains(_toks[i].Text))
            {
                quals.Add(_toks[i].Text);
                i++;
            }
            if (i + 1 < to && _toks[i].Kind == TokKind.Ident && ParseTypeName(_toks[i].Text) is { } declType
                && _toks[i + 1].Kind == TokKind.Ident)
            {
                Tok typeTok = _toks[i];
                int j = i + 2;
                if (j < to && _toks[j].Text == "[")
                {
                    if (j + 2 < to && _toks[j + 1].Kind == TokKind.Number && _toks[j + 2].Text == "]") j += 3;
                    else return false;
                }
                bool rename = IsXSpelledMatrix(typeTok.Text);
                if (rename && quals.Any(InterfaceQualifiers.Contains))
                {
                    // A non-square uniform / attribute / varying changes the interface the runtime
                    // binds; never re-typed here (non-square uniforms are refused upstream, SD0210).
                    return false;
                }
                if (j == to)
                {
                    if (rename) _edits.Add((typeTok.Start, typeTok.End, PhysName(declType)));
                    return true;
                }
                if (_toks[j].Text != "=") return false;
                if (!TryLowerRange(j + 1, to, out Lowered init, out Node? initNode)) return false;
                if (declType.IsNonSquare && init.Type != declType && !(_toks[i + 2].Text == "[")) return false;
                if (rename) _edits.Add((typeTok.Start, typeTok.End, PhysName(declType)));
                AddEdit(initNode!, init.Text);
                return true;
            }
            if (quals.Count > 0) return false;
            return LowerExpressionRange(from, to);
        }

        private bool LowerExpressionRange(int from, int to)
        {
            if (!TryLowerRange(from, to, out Lowered lowered, out Node? node)) return false;
            AddEdit(node!, lowered.Text);
            return true;
        }

        private bool TryLowerRange(int from, int to, out Lowered lowered, out Node? node)
        {
            lowered = default;
            var parser = new Parser(_toks, from, to);
            node = parser.ParseExpression();
            if (node is null || !parser.AtEnd) return false;
            Lowered? l = Lower(node, consumed: false);
            if (l is null) return false;
            lowered = l.Value;
            return true;
        }

        private void AddEdit(Node node, string text)
        {
            if (!string.Equals(text, Original(node), StringComparison.Ordinal))
            {
                _edits.Add((node.Start, node.End, text));
            }
        }

        private string Original(Node n) => body[n.Start..n.End];

        /// <summary>The node's original text with each child span replaced by its lowered text.</summary>
        private string Splice(Node n, params (Node Child, string Text)[] children)
        {
            var sb = new StringBuilder();
            int at = n.Start;
            foreach (var (child, text) in children.OrderBy(c => c.Child.Start))
            {
                sb.Append(body, at, child.Start - at).Append(text);
                at = child.End;
            }
            sb.Append(body, at, n.End - at);
            return sb.ToString();
        }

        // `consumed`: the parent reads only components / columns of this value (an index or a
        // swizzle), so a padded vector need not be narrowed back to its logical width.
        private Lowered? Lower(Node n, bool consumed)
        {
            switch (n)
            {
                case LiteralNode lit:
                    return new Lowered(Original(n), lit.Type);

                case IdentNode id:
                    return new Lowered(Original(n), TypeOfIdent(id.Name));

                case ParenNode p:
                {
                    Lowered? inner = Lower(p.Inner, consumed);
                    if (inner is null) return null;
                    return new Lowered(Splice(n, (p.Inner, inner.Value.Text)), inner.Value.Type);
                }

                case FieldNode f:
                {
                    Lowered? b = Lower(f.Base, consumed: true);
                    if (b is null) return null;
                    GType? bt = b.Value.Type;
                    if (bt is { IsMatrix: true }) return null; // no swizzle on a matrix
                    GType? t = null;
                    if (bt is not null && (bt.IsVector || bt.IsScalar) && IsSwizzle(f.Name))
                    {
                        t = GType.Vec(bt.Base, f.Name.Length);
                    }
                    return new Lowered(Splice(n, (f.Base, b.Value.Text)), t);
                }

                case IndexNode ix:
                {
                    Lowered? b = Lower(ix.Base, consumed: true);
                    Lowered? i = Lower(ix.Idx, consumed: false);
                    if (b is null || i is null) return null;
                    if (i.Value.Type is { IsMatrix: true }) return null;
                    GType? bt = b.Value.Type;
                    string text = Splice(n, (ix.Base, b.Value.Text), (ix.Idx, i.Value.Text));
                    if (bt is null) return new Lowered(text, null);
                    if (bt.IsArray) return new Lowered(text, StripArray(bt.Element!));
                    if (bt.IsVector) return new Lowered(text, GType.Vec(bt.Base, 1));
                    if (bt.IsMatrix)
                    {
                        GType col = GType.Vec('f', bt.Rows);
                        if (bt.IsNonSquare && bt.Rows < bt.Phys && !consumed)
                        {
                            text += "." + Swz[..bt.Rows];
                        }
                        return new Lowered(text, col);
                    }
                    return new Lowered(text, null);
                }

                case UnaryNode u:
                {
                    Lowered? o = Lower(u.Operand, consumed: false);
                    if (o is null) return null;
                    GType? t = o.Value.Type;
                    if (t is { IsNonSquare: true } && u.Op is not ("-" or "+")) return null;
                    if (u.Op == "!") t = GType.Bool;
                    return new Lowered(Splice(n, (u.Operand, o.Value.Text)), t);
                }

                case TernaryNode tn:
                {
                    Lowered? c = Lower(tn.C, consumed: false);
                    Lowered? a = Lower(tn.A, consumed: false);
                    Lowered? b = Lower(tn.B, consumed: false);
                    if (c is null || a is null || b is null) return null;
                    GType? t = a.Value.Type ?? b.Value.Type;
                    if ((a.Value.Type is { IsNonSquare: true } || b.Value.Type is { IsNonSquare: true })
                        && a.Value.Type != b.Value.Type)
                    {
                        return null;
                    }
                    return new Lowered(
                        Splice(n, (tn.C, c.Value.Text), (tn.A, a.Value.Text), (tn.B, b.Value.Text)), t);
                }

                case AssignNode asg:
                    return LowerAssign(asg);

                case BinaryNode bin:
                    return LowerBinary(bin, consumed);

                case CallNode call:
                    return LowerCall(call);
            }
            return null;
        }

        private GType? TypeOfIdent(string name)
        {
            if (_vars.TryGetValue(name, out GType? t)) return t;
            return name switch
            {
                "gl_Position" or "gl_FragColor" or "gl_FragCoord" => GType.Vec4,
                "gl_FragData" => GType.Vec4 with { Element = GType.Vec4 },
                "gl_PointSize" => GType.Float,
                "gl_FrontFacing" => GType.Bool,
                "gl_PointCoord" => GType.Vec('f', 2),
                _ when Regex.IsMatch(name, @"^ps_oC\d+$") => GType.Vec4,
                _ => null,
            };
        }

        private static bool IsSwizzle(string name) =>
            name.Length is >= 1 and <= 4
            && (name.All(c => "xyzw".Contains(c)) || name.All(c => "rgba".Contains(c)) || name.All(c => "stpq".Contains(c)));

        private static string PadVec(string text, int from, int to)
        {
            if (from == to) return text;
            return $"vec{to}({text}" + string.Concat(Enumerable.Repeat(", 0.0", to - from)) + ")";
        }

        private static readonly Regex SideEffect = new(@"\+\+|--|(?<![=!<>])=(?!=)", RegexOptions.Compiled);

        /// <summary>
        /// The text of a matrix value, safe to repeat once per column (no side effect) and to
        /// index (wrapped in parentheses unless it is already a postfix expression), or null.
        /// </summary>
        private static string? Repeatable(string text, Node? node)
        {
            if (SideEffect.IsMatch(text)) return null;
            bool postfixSafe = node is IdentNode or CallNode or IndexNode or ParenNode or FieldNode
                               || (node is null && text.StartsWith('(') && text.EndsWith(')'));
            return postfixSafe ? text : $"({text})";
        }

        /// <summary>A matrix held at <paramref name="from"/>, re-held zero-padded at <paramref name="to"/>.</summary>
        private static string? Widen(string text, Node node, int from, int to)
        {
            string? x = Repeatable(text, node);
            if (x is null) return null;
            var cols = Enumerable.Range(0, to).Select(c => c < from ? PadVec($"{x}[{c}]", from, to) : $"vec{to}(0.0)");
            return $"mat{to}({string.Join(", ", cols)})";
        }

        /// <summary>A matrix held at <paramref name="from"/>, re-held at its own size (a smaller square, or its padded size).</summary>
        private static string? Narrow(string text, int from, GType logical)
        {
            string? x = Repeatable(text, null);
            if (x is null) return null;
            int n = logical.Phys;
            var cols = Enumerable.Range(0, n).Select(c =>
            {
                if (c >= logical.Cols) return $"vec{n}(0.0)";
                string col = logical.Rows < from ? $"{x}[{c}].{Swz[..logical.Rows]}" : $"{x}[{c}]";
                return PadVec(col, logical.Rows, n);
            });
            return $"mat{n}({string.Join(", ", cols)})";
        }

        private Lowered? LowerAssign(AssignNode asg)
        {
            Lowered? l = Lower(asg.L, consumed: false);
            Lowered? r = Lower(asg.R, consumed: false);
            if (l is null || r is null) return null;
            GType? lt = l.Value.Type, rt = r.Value.Type;
            bool lns = lt is { IsNonSquare: true }, rns = rt is { IsNonSquare: true };
            if (lns || rns)
            {
                bool ok = asg.Op switch
                {
                    "=" or "+=" or "-=" => lt == rt,
                    "*=" => lns && (rt is { IsScalar: true }
                                   || (rt is { IsMatrix: true } && rt.Phys == lt!.Phys && rt.Cols == lt.Cols && rt.Rows == lt.Cols)),
                    "/=" => lns && rt is { IsScalar: true },
                    _ => false,
                };
                if (!ok) return null;
            }
            return new Lowered(Splice(asg, (asg.L, l.Value.Text), (asg.R, r.Value.Text)), lt);
        }

        private Lowered? LowerBinary(BinaryNode bin, bool consumed)
        {
            Lowered? l = Lower(bin.L, consumed: false);
            Lowered? r = Lower(bin.R, consumed: false);
            if (l is null || r is null) return null;
            GType? lt = l.Value.Type, rt = r.Value.Type;
            string lx = l.Value.Text, rx = r.Value.Text;
            bool lns = lt is { IsNonSquare: true }, rns = rt is { IsNonSquare: true };

            if (bin.Op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "^^")
            {
                if ((lns || rns) && (bin.Op is not ("==" or "!=") || lt != rt)) return null;
                return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), GType.Bool);
            }
            if (bin.Op == ",")
            {
                return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), rt);
            }

            if (!lns && !rns)
            {
                return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), ArithmeticType(bin.Op, lt, rt));
            }
            if (lt is null || rt is null) return null;

            switch (bin.Op)
            {
                case "+" or "-":
                    // Matrix ± scalar would write the scalar into the padding.
                    if (lt != rt) return null;
                    return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), lt);

                case "/":
                    if (!(lns && rt.IsScalar)) return null;
                    return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), lt);

                case "*":
                    if (lt.IsScalar || rt.IsScalar)
                    {
                        return new Lowered(Splice(bin, (bin.L, lx), (bin.R, rx)), lt.IsScalar ? rt : lt);
                    }
                    if (lt.IsMatrix && rt.IsVector)
                    {
                        // M * v: v is a vecC; the product is a vecR.
                        if (rt.Rows != lt.Cols || rt.Base != 'f') return null;
                        int p = lt.Phys;
                        string text = Splice(bin, (bin.L, lx), (bin.R, PadVec(rx, rt.Rows, p)));
                        GType res = GType.Vec('f', lt.Rows);
                        return new Lowered(lt.Rows < p && !consumed ? $"({text}).{Swz[..lt.Rows]}" : text, res);
                    }
                    if (lt.IsVector && rt.IsMatrix)
                    {
                        // v * M: v is a vecR; the product is a vecC.
                        if (lt.Rows != rt.Rows || lt.Base != 'f') return null;
                        int p = rt.Phys;
                        string text = Splice(bin, (bin.L, PadVec(lx, lt.Rows, p)), (bin.R, rx));
                        GType res = GType.Vec('f', rt.Cols);
                        return new Lowered(rt.Cols < p && !consumed ? $"({text}).{Swz[..rt.Cols]}" : text, res);
                    }
                    if (lt.IsMatrix && rt.IsMatrix)
                    {
                        // matCxR * matKxC = matKxR. Two zero-padded matrices held at one size P
                        // multiply to the zero-padded product at P (an inner padding column meets
                        // an inner padding row). So: widen each factor to the largest of the three
                        // sizes, multiply, and narrow the product to its own size if smaller
                        // (mat4x3 * mat3x4 is a mat3, computed at 4).
                        if (lt.Cols != rt.Rows) return null;
                        var res = new GType('f', rt.Cols, lt.Rows);
                        int lp = lt.Phys, rp = rt.Phys, resP = res.Phys;
                        int p = Math.Max(Math.Max(lp, rp), resP);
                        string? lw = lp == p ? lx : Widen(lx, bin.L, lp, p);
                        string? rw = rp == p ? rx : Widen(rx, bin.R, rp, p);
                        if (lw is null || rw is null) return null;
                        string product = Splice(bin, (bin.L, lw), (bin.R, rw));
                        if (resP == p) return new Lowered(product, res);
                        string? narrowed = Narrow($"({product})", p, res);
                        return narrowed is null ? null : new Lowered(narrowed, res);
                    }
                    return null;
            }
            return null;
        }

        private static GType? ArithmeticType(string op, GType? lt, GType? rt)
        {
            if (lt is null || rt is null) return null;
            if (op == "*" && lt.IsMatrix && rt.IsVector) return GType.Vec('f', lt.Rows);
            if (op == "*" && lt.IsVector && rt.IsMatrix) return GType.Vec('f', rt.Cols);
            if (op == "*" && lt.IsMatrix && rt.IsMatrix) return new GType('f', rt.Cols, lt.Rows);
            if (lt.IsScalar) return rt;
            return lt;
        }

        private Lowered? LowerCall(CallNode call)
        {
            var args = new List<Lowered>(call.Args.Count);
            foreach (Node a in call.Args)
            {
                Lowered? l = Lower(a, consumed: false);
                if (l is null) return null;
                args.Add(l.Value);
            }
            string name = call.Callee.Text;
            GType? ctorType = ParseTypeName(name);
            (Node, string)[] Children() => call.Args.Select((a, k) => (a, args[k].Text)).ToArray();

            if (ctorType is { IsMatrix: true })
            {
                return LowerMatrixConstructor(call, ctorType, args, Children);
            }
            bool anyNonSquare = args.Any(a => a.Type is { IsNonSquare: true });
            if (ctorType is not null)
            {
                // A vector / scalar built from a non-square matrix's components would read the padding.
                if (anyNonSquare) return null;
                return new Lowered(Splice(call, Children()), ctorType);
            }

            GType? resultType;
            if (name == "transpose")
            {
                if (args.Count != 1 || args[0].Type is not { IsMatrix: true } m) return anyNonSquare ? null : new Lowered(Splice(call, Children()), null);
                resultType = new GType('f', m.Rows, m.Cols);
            }
            else if (name == "matrixCompMult")
            {
                if (args.Count != 2 || args[0].Type != args[1].Type) return anyNonSquare ? null : new Lowered(Splice(call, Children()), null);
                resultType = args[0].Type;
            }
            else
            {
                if (anyNonSquare) return null;
                resultType = BuiltinResultType(name, args) ?? (_functions.TryGetValue(name, out GType? f) ? f : null);
            }
            return new Lowered(Splice(call, Children()), resultType);
        }

        private static readonly HashSet<string> GenTypeBuiltins = new(StringComparer.Ordinal)
        {
            "radians", "degrees", "sin", "cos", "tan", "asin", "acos", "atan", "pow", "exp", "log",
            "exp2", "log2", "sqrt", "inversesqrt", "abs", "sign", "floor", "ceil", "fract", "mod",
            "min", "max", "clamp", "mix", "step", "smoothstep", "normalize", "faceforward", "reflect",
            "refract", "dFdx", "dFdy", "fwidth", "trunc", "round", "roundEven", "not",
        };

        private static GType? BuiltinResultType(string name, List<Lowered> args)
        {
            if (GenTypeBuiltins.Contains(name))
            {
                if (args.Any(a => a.Type is null)) return null;
                return args.Select(a => a.Type!).FirstOrDefault(t => !t.IsScalar) ?? args.FirstOrDefault().Type;
            }
            switch (name)
            {
                case "length" or "distance" or "dot" or "determinant":
                    return GType.Float;
                case "cross":
                    return GType.Vec('f', 3);
                case "any" or "all":
                    return GType.Bool;
                case "lessThan" or "lessThanEqual" or "greaterThan" or "greaterThanEqual" or "equal" or "notEqual":
                    return args.Count > 0 && args[0].Type is { IsVector: true } v ? GType.Vec('b', v.Rows) : null;
            }
            if (name.StartsWith("texture", StringComparison.Ordinal) || name.StartsWith("shadow", StringComparison.Ordinal))
            {
                return GType.Vec4;
            }
            return null;
        }

        /// <summary>
        /// A matrix constructor. Non-square targets are built padded; a matrix-from-matrix
        /// constructor (GLSL 1.20) is spelled out column by column with GLSL's identity fill.
        /// </summary>
        private Lowered? LowerMatrixConstructor(CallNode call, GType target, List<Lowered> args, Func<(Node, string)[]> children)
        {
            int p = target.Phys;
            int cols = target.Cols, rows = target.Rows;
            string physName = PhysName(target);
            bool hasMatrixArg = args.Any(a => a.Type is { IsMatrix: true });

            if (!target.IsNonSquare && !hasMatrixArg && !IsXSpelledMatrix(call.Callee.Text))
            {
                return new Lowered(Splice(call, children()), target);
            }
            if (args.Any(a => a.Type is null)) return null;

            var columns = new List<string>(p);
            if (hasMatrixArg)
            {
                if (args.Count != 1) return null;
                GType src = args[0].Type!;
                string? sx = Repeatable(args[0].Text, call.Args[0]);
                if (sx is null) return null;
                int srcColLen = src.IsNonSquare ? src.Phys : src.Rows;
                for (int c = 0; c < cols; c++)
                {
                    var comps = new List<string>();
                    if (c < src.Cols)
                    {
                        int take = Math.Min(rows, src.Rows);
                        string col = $"{sx}[{c}]";
                        comps.Add(take == srcColLen ? col : take == 1 ? $"{col}.x" : $"{col}.{Swz[..take]}");
                        for (int r = take; r < rows; r++) comps.Add(r == c ? "1.0" : "0.0");
                    }
                    else
                    {
                        for (int r = 0; r < rows; r++) comps.Add(r == c ? "1.0" : "0.0");
                    }
                    for (int r = rows; r < p; r++) comps.Add("0.0");
                    // A single entry is the whole source column, already p components wide.
                    columns.Add(comps.Count == 1 ? comps[0] : $"vec{p}({string.Join(", ", comps)})");
                }
            }
            else if (args.Count == 1 && args[0].Type!.IsScalar)
            {
                string s = args[0].Text;
                if (call.Args[0] is not (IdentNode or LiteralNode)) return null;
                for (int c = 0; c < cols; c++)
                {
                    var comps = Enumerable.Range(0, p).Select(r => r == c && r < rows ? s : "0.0");
                    columns.Add($"vec{p}({string.Join(", ", comps)})");
                }
            }
            else
            {
                // Components fill the logical columns in order; each column's arguments must end on
                // its boundary (a column is never split across an argument).
                int k = 0;
                for (int c = 0; c < cols; c++)
                {
                    var parts = new List<string>();
                    int filled = 0;
                    while (filled < rows)
                    {
                        if (k >= args.Count) return null;
                        GType t = args[k].Type!;
                        if (t.IsMatrix || t.IsArray || t.Base == 's') return null;
                        filled += t.Rows;
                        parts.Add(args[k].Text);
                        k++;
                    }
                    if (filled != rows) return null;
                    if (parts.Count == 1 && rows == p)
                    {
                        columns.Add(parts[0]);
                    }
                    else if (parts.Count == 1 && args[k - 1].Type!.IsVector)
                    {
                        columns.Add(PadVec(parts[0], rows, p));
                    }
                    else
                    {
                        columns.Add($"vec{p}({string.Join(", ", parts)}" + string.Concat(Enumerable.Repeat(", 0.0", p - rows)) + ")");
                    }
                }
                if (k != args.Count) return null;
            }
            for (int c = cols; c < p; c++) columns.Add($"vec{p}(0.0)");
            return new Lowered($"{physName}({string.Join(", ", columns)})", target);
        }
    }
}
