#nullable enable

using System.Globalization;
using System.Text;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Preprocessing;

namespace ShadowDusk.HLSL;

/// <summary>
/// The default value of one global uniform, read off its HLSL initializer (<c>float R = 1;</c>).
/// </summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Class">The MGFX parameter class: Scalar = 0, Vector = 1, Matrix = 2.</param>
/// <param name="Type">The MGFX parameter type: Bool = 1, Int32 = 2, Single = 3.</param>
/// <param name="Rows">The MGFX row count (1 for a scalar or vector).</param>
/// <param name="Columns">The MGFX column count (1 for a scalar).</param>
/// <param name="ArrayLength">The declared array length, or 0 when the declaration is not an array.</param>
/// <param name="ElementBlobs">
/// One default blob per array element (a single blob when not an array), each
/// <c>Rows * Columns * 4</c> bytes in the layout MonoGame's <c>EffectParameter.Data</c> holds.
/// </param>
/// <param name="File">The author's file the declaration is in.</param>
/// <param name="Line">The 1-based line of the declared name.</param>
/// <param name="Column">The 1-based column of the declared name.</param>
public sealed record GlobalInitializerDefault(
    string Name,
    byte Class,
    byte Type,
    int Rows,
    int Columns,
    int ArrayLength,
    IReadOnlyList<byte[]> ElementBlobs,
    string File,
    int Line,
    int Column)
{
    /// <summary>
    /// The 1-based line of the initializer's last character in <see cref="File"/>; with
    /// <see cref="EndColumn"/> it closes the span <c>(Line, Column)</c> .. <c>(EndLine, EndColumn)</c>
    /// that runs from the declared name to the end of its initializer. A compiler reports a
    /// diagnostic about the initializer somewhere inside that span (DXC's SPIR-V path at the
    /// initializer's first token, its DXIL path at the name), which can be lines below the name
    /// (<c>float4 C =</c> / <c>{ 1, 0, 0, 1 };</c>). 0 when unknown: the span is the name alone.
    /// </summary>
    public int EndLine { get; init; }

    /// <summary>The 1-based column of the initializer's last character; see <see cref="EndLine"/>.</summary>
    public int EndColumn { get; init; }
}

/// <summary>A global initializer the reader found but could not turn into a default.</summary>
/// <param name="Name">The declared name.</param>
/// <param name="File">The author's file the declaration is in.</param>
/// <param name="Line">The 1-based line of the declared name.</param>
/// <param name="Column">The 1-based column of the declared name.</param>
/// <param name="Reason">Why the initializer could not be evaluated.</param>
public sealed record GlobalInitializerFailure(string Name, string File, int Line, int Column, string Reason);

/// <summary>What <see cref="GlobalInitializerReader.Read"/> found.</summary>
/// <param name="Defaults">Name -> default, for every initializer that was evaluated (last declaration wins).</param>
/// <param name="Failures">Every initializer on a possible uniform that could not be evaluated.</param>
/// <param name="CompilerPredefinedMacro">
/// The first conditional that tested a macro only the compiler defines (<c>__HLSL_VERSION</c> ...), or
/// null. The managed preprocessor evaluated it as undefined, so the branch it picked may not be the one
/// DXC compiled and every default read from it is unverified.
/// </param>
public sealed record GlobalInitializers(
    IReadOnlyDictionary<string, GlobalInitializerDefault> Defaults,
    IReadOnlyList<GlobalInitializerFailure> Failures,
    string? CompilerPredefinedMacro = null);

/// <summary>
/// Reads the initializers of global (and <c>cbuffer</c>) numeric declarations out of an HLSL
/// effect and evaluates them to the default-value blobs an <c>.mgfx</c> / <c>.knifx</c> carries.
///
/// <para><b>Why this exists.</b> DXC drops the initializer of a global that lands in
/// <c>$Globals</c> (it only warns), and vkd3d's DXBC does not carry it either, so nothing
/// downstream of the compilers knows <c>float R = 1;</c> had a value. <c>fxc</c> (and so
/// <c>mgfxc</c> on DirectX) does keep it. The initializer has to come from the source: this reads
/// the preprocessed text the compilers are given (macros expanded, conditionals evaluated, the
/// compile's own defines honoured), finds declaration-scope numeric declarations, and evaluates
/// the initializer itself.</para>
///
/// <para><b>What it evaluates.</b> Numeric literals (decimal, hex, exponents, <c>f/h/u/l</c>
/// suffixes), <c>true</c>/<c>false</c>, the arithmetic, shift, bitwise, comparison, logical and
/// ternary operators, <c>(type)</c> casts, scalar / vector / matrix constructors
/// (<c>float4(1,0,0,1)</c>, <c>float2x2(...)</c>, <c>float4(v.xy, 0, 1)</c>), swizzles, constant
/// indexing, brace lists, arrays of any of those, earlier <c>const</c> / <c>static const</c>
/// scalars, and a small set of math intrinsics. Float arithmetic rounds to 32 bits at every step.
/// Anything else (a function call it does not model, a struct, a texture) is reported in
/// <see cref="GlobalInitializers.Failures"/>, never silently turned into zero.</para>
///
/// <para><b>Blob layout.</b> Exactly what MonoGame's <c>EffectParameter.SetValue</c> would leave in
/// <c>Data</c>: scalars and vectors are their components; a matrix is written column by column
/// (<c>Data[c * Rows + r] = M[r][c]</c>, the "HLSL expects matrices to be transposed" layout);
/// <c>int</c> is its 32-bit value, and <c>bool</c> is <c>0xFFFFFFFF</c> for true, the value
/// D3D reflection hands <c>mgfxc</c>.</para>
/// </summary>
public static class GlobalInitializerReader
{
    /// <summary>
    /// Reads the global initializers of <paramref name="flattenedSource"/>: the effect with its
    /// <c>#include</c>s inlined and the compile's macros prepended as <c>#define</c> lines
    /// (the output of <c>Preprocessor.Flatten</c>, optionally after the FX pre-parse).
    /// </summary>
    /// <param name="flattenedSource">The flattened source.</param>
    /// <param name="sourceFile">Display name used in diagnostics.</param>
    /// <returns>The defaults and failures, or an <c>SD0009</c> error when the preprocessed view cannot be built.</returns>
    public static Result<GlobalInitializers, ShaderError> Read(string flattenedSource, string sourceFile)
    {
        ArgumentNullException.ThrowIfNull(flattenedSource);

        Result<FxMacroPreprocessor.CompilerInput, ShaderError> view =
            FxMacroPreprocessor.ProcessForCompiler(flattenedSource, sourceFile);
        if (view.IsFailure)
            return Result<GlobalInitializers, ShaderError>.Fail(view.Error);

        GlobalInitializers read = ReadPreprocessed(view.Value.Text, sourceFile);
        if (view.Value.PredefinedMacroUse is { } use)
            read = read with { CompilerPredefinedMacro = $"{use.Name} ({use.File}, line {use.Line})" };
        return Result<GlobalInitializers, ShaderError>.Ok(read);
    }

    /// <summary>
    /// The same as <see cref="Read"/> on text that is already preprocessed (macros expanded,
    /// conditionals evaluated). Exposed for unit tests.
    /// </summary>
    internal static GlobalInitializers ReadPreprocessed(string preprocessed, string sourceFile)
    {
        List<Tk> tokens = Lex(preprocessed, sourceFile);
        var scanner = new Scanner(tokens);
        scanner.Run();
        return new GlobalInitializers(scanner.Defaults, scanner.Failures);
    }

    // =====================================================================================
    // Lexer
    // =====================================================================================

    private enum K { Ident, Number, Punct, Str, Eof }

    private sealed record Tk(K Kind, string Text, string File, int Line, int Col);

    private static List<Tk> Lex(string text, string file)
    {
        var tokens = new List<Tk>();
        string curFile = file;
        int line = 1;
        int i = 0;
        int n = text.Length;
        bool lineStart = true;
        int lineStartIndex = 0;

        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { line++; i++; lineStart = true; lineStartIndex = i; continue; }
            if (c == '\r' || c == ' ' || c == '\t' || c == '\f' || c == '\v') { i++; continue; }

            int col = i - lineStartIndex + 1;

            if (c == '#' && lineStart)
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = n;
                ApplyLineDirective(text.AsSpan(i, end - i), ref curFile, ref line);
                i = end;           // the newline itself bumps the line
                continue;
            }
            lineStart = false;

            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = close < 0 ? n : close + 2;
                for (int j = i; j < stop; j++)
                    if (text[j] == '\n') { line++; lineStartIndex = j + 1; }
                i = stop;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                tokens.Add(new Tk(K.Ident, text[s..i], curFile, line, col));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(text[i + 1])))
            {
                int s = i;
                if (c == '0' && i + 1 < n && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && Uri.IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < n && char.IsDigit(text[i])) i++;
                    if (i < n && text[i] == '.') { i++; while (i < n && char.IsDigit(text[i])) i++; }
                    if (i < n && (text[i] == 'e' || text[i] == 'E'))
                    {
                        int save = i;
                        i++;
                        if (i < n && (text[i] == '+' || text[i] == '-')) i++;
                        if (i < n && char.IsDigit(text[i])) { while (i < n && char.IsDigit(text[i])) i++; }
                        else i = save;
                    }
                }
                while (i < n && (char.IsLetter(text[i]))) i++;   // f F h H u U l L suffixes
                tokens.Add(new Tk(K.Number, text[s..i], curFile, line, col));
                continue;
            }

            if (c == '"')
            {
                int s = i++;
                while (i < n && text[i] != '"' && text[i] != '\n') i++;
                if (i < n && text[i] == '"') i++;
                tokens.Add(new Tk(K.Str, text[s..i], curFile, line, col));
                continue;
            }

            // Multi-character operators the evaluator needs.
            string? two = i + 1 < n ? text.Substring(i, 2) : null;
            if (two is "<<" or ">>" or "<=" or ">=" or "==" or "!=" or "&&" or "||" or "::")
            {
                tokens.Add(new Tk(K.Punct, two, curFile, line, col));
                i += 2;
                continue;
            }
            tokens.Add(new Tk(K.Punct, c.ToString(), curFile, line, col));
            i++;
        }

        tokens.Add(new Tk(K.Eof, "", curFile, line, 1));
        return tokens;
    }

    // '#line N "file"' / '# N "file"': the NEXT physical line is N, in "file".
    private static void ApplyLineDirective(ReadOnlySpan<char> directive, ref string file, ref int line)
    {
        string d = directive.ToString().Trim().TrimStart('#').Trim();
        if (d.StartsWith("line", StringComparison.Ordinal))
            d = d[4..].Trim();
        else if (d.Length == 0 || !char.IsDigit(d[0]))
            return;

        int sp = 0;
        while (sp < d.Length && char.IsDigit(d[sp])) sp++;
        if (sp == 0 || !int.TryParse(d[..sp], NumberStyles.None, CultureInfo.InvariantCulture, out int next))
            return;

        // The directive's own newline will add 1, so land one short.
        line = next - 1;
        int q1 = d.IndexOf('"');
        int q2 = q1 < 0 ? -1 : d.IndexOf('"', q1 + 1);
        if (q1 >= 0 && q2 > q1)
            file = d[(q1 + 1)..q2];
    }

    // =====================================================================================
    // Type model
    // =====================================================================================

    private enum Kind { Float, Int, UInt, Bool }

    private readonly record struct NumType(Kind Kind, int Rows, int Cols, bool IsMatrix, bool IsScalar)
    {
        public int Count => Rows * Cols;
        public byte MgfxClass => IsMatrix ? (byte)2 : IsScalar ? (byte)0 : (byte)1;
        public byte MgfxType => Kind switch { Kind.Bool => (byte)1, Kind.Int or Kind.UInt => (byte)2, _ => (byte)3 };
    }

    private static bool TryParseBaseKind(string name, out Kind kind)
    {
        switch (name)
        {
            case "float": case "half": case "min16float": case "min10float": case "float32_t":
                kind = Kind.Float; return true;
            case "int": case "min16int": case "min12int": case "int32_t":
                kind = Kind.Int; return true;
            case "uint": case "dword": case "min16uint": case "uint32_t":
                kind = Kind.UInt; return true;
            case "bool":
                kind = Kind.Bool; return true;
            default:
                kind = default; return false;
        }
    }

    /// <summary>Parses <c>float</c>, <c>int3</c>, <c>float4x4</c> ... (not the <c>vector&lt;&gt;</c> forms).</summary>
    private static bool TryParseTypeName(string name, out NumType type)
    {
        type = default;
        int end = name.Length;
        int d1 = 0, d2 = 0;

        // Trailing "<r>x<c>" or "<n>".
        if (end >= 3 && char.IsDigit(name[end - 1]) && name[end - 2] == 'x' && char.IsDigit(name[end - 3]))
        {
            d1 = name[end - 3] - '0';
            d2 = name[end - 1] - '0';
            end -= 3;
        }
        else if (end >= 2 && char.IsDigit(name[end - 1]) && !char.IsDigit(name[end - 2]))
        {
            d1 = name[end - 1] - '0';
            end -= 1;
        }

        if (!TryParseBaseKind(name[..end], out Kind kind)) return false;

        if (d2 != 0)
        {
            if (d1 is < 1 or > 4 || d2 is < 1 or > 4) return false;
            type = new NumType(kind, d1, d2, IsMatrix: true, IsScalar: false);
            return true;
        }
        if (d1 != 0)
        {
            if (d1 is < 1 or > 4) return false;
            // D3D reflection reports float1 as a scalar.
            type = new NumType(kind, 1, d1, IsMatrix: false, IsScalar: d1 == 1);
            return true;
        }
        type = new NumType(kind, 1, 1, IsMatrix: false, IsScalar: true);
        return true;
    }

    // =====================================================================================
    // Values
    // =====================================================================================

    private sealed class EvalException(string message) : Exception(message);

    /// <summary>A numeric value: a flat row-major component list of one kind, with a shape.</summary>
    private sealed record Val(Kind Kind, int Rows, int Cols, bool IsMatrix, bool IsScalar, double[] C)
    {
        public int Count => C.Length;

        public static Val Scalar(Kind k, double v) => new(k, 1, 1, false, true, [v]);

        public static Val OfType(NumType t, double[] c) => new(t.Kind, t.Rows, t.Cols, t.IsMatrix, t.IsScalar, c);

        public NumType Type => new(Kind, Rows, Cols, IsMatrix, IsScalar);
    }

    private static double Norm(Kind k, double v)
    {
        switch (k)
        {
            case Kind.Float:
                return (double)(float)v;
            case Kind.Int:
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
                return unchecked((int)(long)Math.Truncate(Math.Max(-9.3e18, Math.Min(9.3e18, v))));
            case Kind.UInt:
                if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
                return unchecked((uint)(long)Math.Truncate(Math.Max(-9.3e18, Math.Min(9.3e18, v))));
            default:
                return v != 0 ? 1 : 0;
        }
    }

    private static Val ConvertKind(Val v, Kind k)
    {
        if (v.Kind == k) return v;
        var c = new double[v.Count];
        for (int i = 0; i < c.Length; i++) c[i] = Norm(k, v.C[i]);
        return v with { Kind = k, C = c };
    }

    // =====================================================================================
    // Scanner: declaration-scope statements
    // =====================================================================================

    private const int MaxArrayLength = 4096;

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "uniform", "const", "static", "extern", "shared", "volatile", "precise", "row_major",
        "column_major", "nointerpolation", "linear", "centroid", "noperspective", "sample",
        "groupshared", "inline", "snorm", "unorm",
    };

    private sealed class Scanner
    {
        private readonly List<Tk> _t;
        private int _p;

        // Earlier const scalars/vectors an initializer may reference.
        private readonly Dictionary<string, Val> _consts = new(StringComparer.Ordinal);

        // 'typedef float4 col_t;' names the scanner can resolve to a numeric type.
        private readonly Dictionary<string, NumType> _aliases = new(StringComparer.Ordinal);

        public Dictionary<string, GlobalInitializerDefault> Defaults { get; } = new(StringComparer.Ordinal);
        public List<GlobalInitializerFailure> Failures { get; } = [];

        public Scanner(List<Tk> tokens) => _t = tokens;

        private Tk Cur => _t[_p];
        private Tk At(int i) => _t[Math.Min(i, _t.Count - 1)];
        private bool Is(string punct) => Cur.Kind == K.Punct && Cur.Text == punct;

        public void Run()
        {
            while (Cur.Kind != K.Eof)
                Statement(inBuffer: false);
        }

        // One statement at declaration scope (top level, or inside a cbuffer/tbuffer body).
        private void Statement(bool inBuffer)
        {
            if (Is(";")) { _p++; return; }
            if (inBuffer && Is("}")) return;

            int start = _p;

            // cbuffer / tbuffer NAME [: register(..)] { declarations }
            if (!inBuffer && Cur.Kind == K.Ident && Cur.Text is "cbuffer" or "tbuffer")
            {
                _p++;
                while (Cur.Kind != K.Eof && !Is("{") && !Is(";")) _p++;
                if (Is("{"))
                {
                    _p++;
                    while (Cur.Kind != K.Eof && !Is("}"))
                    {
                        int before = _p;
                        Statement(inBuffer: true);
                        if (_p == before) _p++;   // never stall
                    }
                    if (Is("}")) _p++;
                    if (Is(";")) _p++;
                }
                return;
            }

            // typedef [const] <numeric type> NAME;  -- remembered so 'NAME x = ...;' is read.
            if (Cur.Kind == K.Ident && Cur.Text == "typedef")
            {
                int t = _p + 1;
                while (At(t).Kind == K.Ident && Modifiers.Contains(At(t).Text)) t++;
                if (At(t).Kind == K.Ident && TryParseTypeName(At(t).Text, out NumType aliased) &&
                    At(t + 1).Kind == K.Ident && At(t + 2).Kind == K.Punct && At(t + 2).Text == ";")
                    _aliases[At(t + 1).Text] = aliased;
                SkipStatement();
                return;
            }

            // Collect modifiers.
            bool isStatic = false, isConst = false, isGroupShared = false, isExtern = false;
            int q = _p;
            while (At(q).Kind == K.Ident && Modifiers.Contains(At(q).Text))
            {
                switch (At(q).Text)
                {
                    case "static": isStatic = true; break;
                    case "const": isConst = true; break;
                    case "groupshared": isGroupShared = true; break;
                    case "extern": isExtern = true; break;
                }
                q++;
            }
            _ = isExtern;

            // The type.
            NumType type = default;
            bool numeric = false;
            int afterType = q;
            Tk tt = At(q);
            if (tt.Kind == K.Ident)
            {
                if (tt.Text == "unsigned" && At(q + 1).Kind == K.Ident && At(q + 1).Text is "int" or "dword")
                {
                    type = new NumType(Kind.UInt, 1, 1, IsMatrix: false, IsScalar: true);
                    numeric = true;
                    afterType = q + 2;
                }
                else if (TryParseTypeName(tt.Text, out type) || _aliases.TryGetValue(tt.Text, out type))
                {
                    numeric = true;
                    afterType = q + 1;
                }
                else if (tt.Text is "vector" or "matrix" && At(q + 1).Kind == K.Punct && At(q + 1).Text == "<")
                {
                    if (TryParseTemplateType(q, out type, out int next)) { numeric = true; afterType = next; }
                }
            }

            // 'float const A = 1;' -- a qualifier written after the type.
            while (numeric && At(afterType).Kind == K.Ident && Modifiers.Contains(At(afterType).Text))
            {
                if (At(afterType).Text == "const") isConst = true;
                if (At(afterType).Text == "static") isStatic = true;
                afterType++;
            }

            // A numeric declaration is IDENT [dims] ... ; or IDENT '(' (a function). Decide by
            // what follows the first declarator name.
            if (numeric && At(afterType).Kind == K.Ident && !(At(afterType + 1).Kind == K.Punct && At(afterType + 1).Text == "("))
            {
                _p = afterType;
                Declarators(type, isStatic, isConst, isGroupShared);
                // Declarators leaves _p after the terminating ';'
                return;
            }

            // A declaration with an initializer whose type the evaluator does not know (a typedef it
            // could not resolve, 'double', a struct) is a possible uniform default it cannot carry:
            // say so rather than let the parameter silently default to zero.
            if (!numeric && !isStatic && tt.Kind == K.Ident && At(q + 1).Kind == K.Ident && !IsResourceTypeName(tt.Text))
            {
                int j = q + 2;
                while (At(j).Kind != K.Eof && !(At(j).Kind == K.Punct && At(j).Text is ";" or "{" or "(" or "=")) j++;
                if (At(j).Kind == K.Punct && At(j).Text == "=" &&
                    !(At(j + 1).Kind == K.Ident && At(j + 1).Text is "sampler_state" or "compile" or "SamplerState"))
                {
                    Tk name = At(q + 1);
                    Failures.Add(new GlobalInitializerFailure(name.Text, name.File, name.Line, name.Col,
                        $"the declared type '{tt.Text}' is not one the initializer evaluator models"));
                }
            }

            // Anything else (struct, function, technique, sampler, texture ...): skip it whole.
            _p = start;
            SkipStatement();
        }

        private static bool IsResourceTypeName(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("sampler", StringComparison.Ordinal) || n.Contains("texture", StringComparison.Ordinal)
                || n.Contains("buffer", StringComparison.Ordinal) || n is "string" or "struct" or "class" or "technique"
                or "technique10" or "technique11" or "namespace" or "return" or "typedef";
        }

        private bool TryParseTemplateType(int q, out NumType type, out int next)
        {
            type = default;
            next = q;
            // vector<T, N>   matrix<T, R, C>
            bool matrix = At(q).Text == "matrix";
            int i = q + 2;
            if (At(i).Kind != K.Ident || !TryParseBaseKind(At(i).Text, out Kind kind)) return false;
            i++;
            int[] dims = new int[2];
            int need = matrix ? 2 : 1;
            for (int d = 0; d < need; d++)
            {
                if (!(At(i).Kind == K.Punct && At(i).Text == ",")) return false;
                i++;
                if (At(i).Kind != K.Number || !int.TryParse(At(i).Text, out dims[d])) return false;
                i++;
            }
            if (!(At(i).Kind == K.Punct && At(i).Text == ">")) return false;
            i++;
            if (matrix)
            {
                if (dims[0] is < 1 or > 4 || dims[1] is < 1 or > 4) return false;
                type = new NumType(kind, dims[0], dims[1], true, false);
            }
            else
            {
                if (dims[0] is < 1 or > 4) return false;
                type = new NumType(kind, 1, dims[0], false, false);
            }
            next = i;
            return true;
        }

        // Skips one statement or definition: to the ';' that ends it, or past a '{...}' body
        // that ends it (function, struct, technique), balancing every bracket kind.
        private void SkipStatement()
        {
            int depthParen = 0, depthBracket = 0;
            bool headIsStruct = Cur.Kind == K.Ident && Cur.Text is "struct" or "class" or "typedef";
            bool sawParen = false;
            while (Cur.Kind != K.Eof)
            {
                if (Cur.Kind == K.Punct)
                {
                    switch (Cur.Text)
                    {
                        case "(": depthParen++; sawParen = true; break;
                        case ")": depthParen--; break;
                        case "[": depthBracket++; break;
                        case "]": depthBracket--; break;
                        case ";":
                            if (depthParen <= 0) { _p++; return; }
                            break;
                        case "{":
                            if (depthParen <= 0)
                            {
                                bool afterEquals = _p > 0 && _t[_p - 1].Kind == K.Punct && _t[_p - 1].Text == "=";
                                SkipBraces();
                                // 'x = { ... };' and 'struct S { ... };' end at the ';'; a function
                                // or technique body ends at its '}'.
                                if (Is(";")) { _p++; return; }
                                if (!afterEquals && !(headIsStruct && !sawParen && Cur.Kind == K.Ident)) return;
                                continue;
                            }
                            break;
                    }
                }
                _p++;
            }
        }

        private void SkipBraces()
        {
            int depth = 0;
            while (Cur.Kind != K.Eof)
            {
                if (Is("{")) depth++;
                else if (Is("}")) { depth--; if (depth == 0) { _p++; return; } }
                _p++;
            }
        }

        // One declaration: NAME[dims] [: sem] [<annotations>] [= init] {, NAME ...} ;
        private void Declarators(NumType type, bool isStatic, bool isConst, bool isGroupShared)
        {
            while (true)
            {
                if (Cur.Kind != K.Ident) { SkipStatement(); return; }
                Tk nameTok = Cur;
                _p++;

                // Array dimensions.
                var dims = new List<int>();
                bool dimsOk = true;
                string? dimsError = null;
                while (Is("["))
                {
                    int close = FindMatching(_p, "[", "]");
                    if (close < 0) { SkipStatement(); return; }
                    if (close == _p + 1) { dimsOk = false; dimsError = "an unsized array"; }
                    else
                    {
                        try
                        {
                            var sub = new Evaluator(_t, _p + 1, close, _consts);
                            Val dv = sub.EvalAll();
                            if (dv.Count != 1) throw new EvalException("array length is not a scalar");
                            int len = (int)ConvertKind(dv, Kind.Int).C[0];
                            if (len < 1) throw new EvalException("array length is not positive");
                            if (len > MaxArrayLength) throw new EvalException($"array length {len} is larger than the {MaxArrayLength} elements the reader models");
                            dims.Add(len);
                        }
                        catch (EvalException e) { dimsOk = false; dimsError = $"array length: {e.Message}"; }
                    }
                    _p = close + 1;
                }

                // ': SEMANTIC' / ': register(..)' / ': packoffset(..)'
                while (Is(":"))
                {
                    _p++;
                    if (Cur.Kind == K.Ident) _p++;
                    if (Is("("))
                    {
                        int close = FindMatching(_p, "(", ")");
                        _p = close < 0 ? _t.Count - 1 : close + 1;
                    }
                }

                // '<annotations>' (normally already stripped by the pre-parse).
                if (Is("<"))
                {
                    int depth = 0;
                    while (Cur.Kind != K.Eof)
                    {
                        if (Is("<")) depth++;
                        else if (Is(">")) { depth--; if (depth == 0) { _p++; break; } }
                        _p++;
                    }
                }

                int initStart = -1, initEnd = -1;
                if (Is("="))
                {
                    _p++;
                    initStart = _p;
                    initEnd = FindInitializerEnd(_p);
                    _p = initEnd;
                }

                bool reachable = !isGroupShared;
                if (initStart >= 0 && reachable)
                    Handle(nameTok, type, dims, dimsOk, dimsError, initStart, initEnd, isStatic, isConst);

                if (Is(",")) { _p++; continue; }
                if (Is(";")) { _p++; return; }
                // Unexpected token: resynchronise.
                SkipStatement();
                return;
            }
        }

        private int FindMatching(int open, string o, string c)
        {
            int depth = 0;
            for (int i = open; i < _t.Count; i++)
            {
                if (_t[i].Kind != K.Punct) continue;
                if (_t[i].Text == o) depth++;
                else if (_t[i].Text == c) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        // The index of the ',' or ';' that ends the initializer starting at 'start'.
        private int FindInitializerEnd(int start)
        {
            int paren = 0, brace = 0, bracket = 0;
            int i = start;
            for (; i < _t.Count; i++)
            {
                Tk t = _t[i];
                if (t.Kind == K.Eof) break;
                if (t.Kind != K.Punct) continue;
                switch (t.Text)
                {
                    case "(": paren++; break;
                    case ")": paren--; break;
                    case "{": brace++; break;
                    case "}": brace--; break;
                    case "[": bracket++; break;
                    case "]": bracket--; break;
                    case "," when paren == 0 && brace == 0 && bracket == 0: return i;
                    case ";" when paren <= 0 && brace <= 0: return i;
                }
            }
            return i;
        }

        private void Handle(
            Tk nameTok, NumType type, List<int> dims, bool dimsOk, string? dimsError,
            int initStart, int initEnd, bool isStatic, bool isConst)
        {
            void Fail(string reason)
            {
                // A static is never a uniform; only a possible uniform is worth a failure.
                if (!isStatic)
                    Failures.Add(new GlobalInitializerFailure(nameTok.Text, nameTok.File, nameTok.Line, nameTok.Col, reason));
            }

            if (!dimsOk) { Fail($"unsupported declaration ({dimsError})"); return; }
            if (dims.Count > 1) { Fail("multi-dimensional arrays are not modelled"); return; }
            if (initEnd <= initStart) { Fail("empty initializer"); return; }

            int? arrayLength = dims.Count == 1 ? dims[0] : null;

            try
            {
                var ev = new Evaluator(_t, initStart, initEnd, _consts);
                List<Val> elements = ev.EvalInitializer(type, arrayLength);

                // 'const' / 'static const' non-array values become usable by later initializers.
                if (isConst && arrayLength is null)
                    _consts[nameTok.Text] = elements[0];

                if (isStatic) return;   // not a uniform: no default to carry

                var blobs = new List<byte[]>(elements.Count);
                foreach (Val v in elements) blobs.Add(ToBlob(v));

                // The span ends at the initializer's last token in the declaration's own file (a
                // token a macro or #include brought in from elsewhere cannot bound it).
                Tk end = nameTok;
                for (int i = initEnd - 1; i >= initStart; i--)
                {
                    if (string.Equals(_t[i].File, nameTok.File, StringComparison.Ordinal))
                    {
                        end = _t[i];
                        break;
                    }
                }

                Defaults[nameTok.Text] = new GlobalInitializerDefault(
                    nameTok.Text,
                    type.MgfxClass,
                    type.MgfxType,
                    type.IsMatrix ? type.Rows : 1,
                    type.IsScalar ? 1 : type.Cols,
                    arrayLength ?? 0,
                    blobs,
                    nameTok.File,
                    nameTok.Line,
                    nameTok.Col)
                {
                    EndLine = end.Line,
                    EndColumn = end.Col + Math.Max(end.Text.Length, 1) - 1,
                };
            }
            catch (EvalException e)
            {
                Fail(e.Message);
            }
        }
    }

    // =====================================================================================
    // Blob encoding
    // =====================================================================================

    private static byte[] ToBlob(Val v)
    {
        int count = v.Count;
        var bytes = new byte[count * 4];

        for (int i = 0; i < count; i++)
        {
            // Matrix: Data[c * rows + r] = M[r][c]; the component list is row-major.
            int dst = i;
            if (v.IsMatrix)
            {
                int r = i / v.Cols;
                int c = i % v.Cols;
                dst = c * v.Rows + r;
            }

            byte[] word = v.Kind switch
            {
                Kind.Float => BitConverter.GetBytes((float)v.C[i]),
                Kind.Int or Kind.UInt => BitConverter.GetBytes(unchecked((int)(long)v.C[i])),
                _ => BitConverter.GetBytes(v.C[i] != 0 ? -1 : 0),
            };
            if (!BitConverter.IsLittleEndian) Array.Reverse(word);
            Array.Copy(word, 0, bytes, dst * 4, 4);
        }
        return bytes;
    }

    // =====================================================================================
    // Evaluator
    // =====================================================================================

    private sealed class Evaluator
    {
        // The evaluator is recursive descent over untrusted source and runs on every compile, so
        // nesting is bounded well below any host's stack (the browser's is the smallest).
        private const int MaxDepth = 64;
        private const int MaxPrefixChain = 4096;
        private int _depth;
        private readonly List<Tk> _t;
        private int _p;
        private readonly int _end;
        private readonly IReadOnlyDictionary<string, Val> _consts;

        public Evaluator(List<Tk> tokens, int start, int end, IReadOnlyDictionary<string, Val> consts)
        {
            _t = tokens; _p = start; _end = end; _consts = consts;
        }

        private bool AtEnd => _p >= _end;
        private Tk Cur => AtEnd ? _t[^1] : _t[_p];
        private bool Is(string punct) => !AtEnd && Cur.Kind == K.Punct && Cur.Text == punct;

        private bool Take(string punct)
        {
            if (!Is(punct)) return false;
            _p++;
            return true;
        }

        private void Expect(string punct)
        {
            if (!Take(punct)) throw new EvalException($"expected '{punct}'");
        }

        public Val EvalAll()
        {
            Val v = Expression();
            if (!AtEnd) throw new EvalException($"unexpected '{Cur.Text}'");
            return v;
        }

        /// <summary>
        /// Evaluates an initializer for <paramref name="type"/> (an array of <paramref name="arrayLength"/>
        /// elements when it has a value) into one value per element.
        /// </summary>
        public List<Val> EvalInitializer(NumType type, int? arrayLength)
        {
            if (Is("{"))
            {
                var items = BraceItems();
                if (arrayLength is { } n)
                {
                    // Either one item per element, or the elements' components laid flat.
                    var elems = new List<Val>();
                    if (items.Count == n)
                    {
                        foreach (BraceItem it in items) elems.Add(ConvertItem(it, type));
                        return elems;
                    }
                    var flat = new List<Val>();
                    foreach (BraceItem it in items) CollectFlat(it, flat);
                    int total = flat.Sum(f => f.Count);
                    if (total != (long)n * type.Count)
                        throw new EvalException(
                            $"the array initializer supplies {items.Count} item(s) ({total} component(s)) for {n} element(s) of {type.Count} component(s)");
                    return SplitFlat(flat, type, n);
                }

                var flatOne = new List<Val>();
                foreach (BraceItem it in items) CollectFlat(it, flatOne);
                return [FromFlat(flatOne, type)];
            }

            Val e = Expression();
            if (!AtEnd) throw new EvalException($"unexpected '{Cur.Text}'");

            if (arrayLength is { } len)
            {
                // 'float4 c[2] = float4(...)' is not valid HLSL for len > 1; accept only an exact component match.
                if (e.Count == len * type.Count && len > 1)
                    return SplitFlat([e], type, len);
                throw new EvalException("an array needs a brace list initializer");
            }
            return [Coerce(e, type)];
        }

        // ---- brace lists ------------------------------------------------------------------

        private sealed record BraceItem(Val? Value, List<BraceItem>? Nested);

        // The end-of-initializer check lives on the outermost call only; nested lists are
        // followed by ',' or '}'.
        private List<BraceItem> BraceItems()
        {
            List<BraceItem> items = NestedBraceItems();
            if (!AtEnd) throw new EvalException($"unexpected '{Cur.Text}' after the initializer list");
            return items;
        }

        private List<BraceItem> NestedBraceItems()
        {
            Expect("{");
            var items = new List<BraceItem>();
            while (!Is("}"))
            {
                if (AtEnd) throw new EvalException("unterminated '{' initializer list");
                if (Is("{")) items.Add(new BraceItem(null, NestedBraceItems()));
                else items.Add(new BraceItem(Expression(), null));
                if (!Take(",")) break;
            }
            Expect("}");
            return items;
        }

        private static void CollectFlat(BraceItem it, List<Val> into)
        {
            if (it.Value is not null) into.Add(it.Value);
            else foreach (BraceItem n in it.Nested!) CollectFlat(n, into);
        }

        private static Val ConvertItem(BraceItem it, NumType type)
        {
            if (it.Value is not null) return Coerce(it.Value, type);
            var flat = new List<Val>();
            CollectFlat(it, flat);
            return FromFlat(flat, type);
        }

        private static List<Val> SplitFlat(List<Val> parts, NumType type, int n)
        {
            var comps = new List<double>();
            Kind src = Kind.Int;
            bool any = false;
            foreach (Val p in parts)
            {
                if (!any) { src = p.Kind; any = true; }
                else if (p.Kind == Kind.Float || src == Kind.Float) src = Kind.Float;
                comps.AddRange(p.C);
            }
            var result = new List<Val>(n);
            for (int i = 0; i < n; i++)
            {
                var c = comps.GetRange(i * type.Count, type.Count).Select(x => Norm(type.Kind, x)).ToArray();
                result.Add(Val.OfType(type, c));
            }
            return result;
        }

        private static Val FromFlat(List<Val> parts, NumType type)
        {
            var comps = new List<double>();
            foreach (Val p in parts) comps.AddRange(p.C);
            if (comps.Count == 1 && type.Count > 1)
                return Splat(new Val(parts[0].Kind, 1, 1, false, true, [comps[0]]), type);
            if (comps.Count != type.Count)
                throw new EvalException(
                    $"the initializer supplies {comps.Count} component(s) for a type with {type.Count}");
            return Val.OfType(type, comps.Select(x => Norm(type.Kind, x)).ToArray());
        }

        // ---- implicit conversion to a declared type ---------------------------------------

        private static Val Coerce(Val v, NumType type)
        {
            if (v.Count == 1 && (type.Count > 1 || !type.IsScalar && type.Count == 1))
                return Splat(v, type);
            if (v.Count == type.Count)
            {
                // Same component count. A vector assigned to a matrix (or the reverse) is a shape error.
                if (v.IsMatrix != type.IsMatrix && !(v.IsScalar && type.IsScalar))
                    throw new EvalException("the initializer's shape (vector vs matrix) does not match the declared type");
                if (v.IsMatrix && (v.Rows != type.Rows || v.Cols != type.Cols))
                    throw new EvalException($"the initializer is a {v.Rows}x{v.Cols} matrix, the declaration a {type.Rows}x{type.Cols}");
                return Val.OfType(type, v.C.Select(x => Norm(type.Kind, x)).ToArray());
            }
            throw new EvalException(
                $"the initializer has {v.Count} component(s) but the declared type has {type.Count}");
        }

        private static Val Splat(Val v, NumType type)
        {
            double x = Norm(type.Kind, v.C[0]);
            var c = new double[type.Count];
            Array.Fill(c, x);
            return Val.OfType(type, c);
        }

        // ---- expressions ------------------------------------------------------------------

        private Val Expression()
        {
            if (++_depth > MaxDepth) throw new EvalException("the initializer is nested too deeply");
            try { return Ternary(); }
            finally { _depth--; }
        }

        private Val Ternary()
        {
            Val cond = Binary(0);
            if (Take("?"))
            {
                Val a = Ternary();
                Expect(":");
                Val b = Ternary();
                if (cond.Count != 1) throw new EvalException("a vector ternary condition is not modelled");

                // Both arms are evaluated (and must be evaluable); the result takes the common type of the two.
                Kind common = a.Kind == Kind.Float || b.Kind == Kind.Float ? Kind.Float
                    : a.Kind == Kind.UInt || b.Kind == Kind.UInt ? Kind.UInt
                    : a.Kind == Kind.Int || b.Kind == Kind.Int ? Kind.Int : Kind.Bool;
                return ConvertKind(cond.C[0] != 0 ? a : b, common);
            }
            return cond;
        }

        private static readonly string[][] Levels =
        [
            ["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
        ];

        private Val Binary(int level)
        {
            if (level >= Levels.Length) return Unary();
            Val left = Binary(level + 1);
            while (Cur.Kind == K.Punct && !AtEnd && Array.IndexOf(Levels[level], Cur.Text) >= 0)
            {
                string op = Cur.Text;
                _p++;
                Val right = Binary(level + 1);
                left = Apply(op, left, right);
            }
            return left;
        }

        private static Val Apply(string op, Val a, Val b)
        {
            // Shape: equal shapes, or a scalar broadcast.
            Val shape;
            if (a.Count == b.Count) shape = a.IsScalar && !b.IsScalar ? b : a;
            else if (a.Count == 1) shape = b;
            else if (b.Count == 1) shape = a;
            else throw new EvalException($"operands of '{op}' have different shapes");

            int n = shape.Count;
            bool logical = op is "||" or "&&";
            bool compare = op is "==" or "!=" or "<" or ">" or "<=" or ">=";
            bool intOnly = op is "|" or "^" or "&" or "<<" or ">>" or "%";

            bool asFloat = a.Kind == Kind.Float || b.Kind == Kind.Float;
            Kind resultKind;
            if (logical || compare) resultKind = Kind.Bool;
            else if (asFloat)
            {
                if (intOnly && op != "%") throw new EvalException($"'{op}' needs integer operands");
                resultKind = Kind.Float;
            }
            else resultKind = a.Kind == Kind.UInt || b.Kind == Kind.UInt ? Kind.UInt : Kind.Int;

            var c = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = a.Count == 1 ? a.C[0] : a.C[i];
                double y = b.Count == 1 ? b.C[0] : b.C[i];
                double r;
                switch (op)
                {
                    case "||": r = (x != 0 || y != 0) ? 1 : 0; break;
                    case "&&": r = (x != 0 && y != 0) ? 1 : 0; break;
                    case "==": r = x == y ? 1 : 0; break;
                    case "!=": r = x != y ? 1 : 0; break;
                    case "<": r = x < y ? 1 : 0; break;
                    case ">": r = x > y ? 1 : 0; break;
                    case "<=": r = x <= y ? 1 : 0; break;
                    case ">=": r = x >= y ? 1 : 0; break;
                    default:
                        if (asFloat)
                        {
                            float fx = (float)x, fy = (float)y;
                            r = op switch
                            {
                                "+" => fx + fy,
                                "-" => fx - fy,
                                "*" => fx * fy,
                                "/" => fx / fy,
                                "%" => fx % fy,
                                _ => throw new EvalException($"operator '{op}' is not modelled"),
                            };
                        }
                        else
                        {
                            // Integers: values are held as exact doubles (int32 or uint32 range), so long math is exact.
                            long lx = (long)x, ly = (long)y;
                            long lr;
                            switch (op)
                            {
                                case "+": lr = lx + ly; break;
                                case "-": lr = lx - ly; break;
                                case "*": lr = lx * ly; break;
                                case "/":
                                    if (ly == 0) throw new EvalException("integer division by zero");
                                    lr = lx / ly; break;
                                case "%":
                                    if (ly == 0) throw new EvalException("integer modulo by zero");
                                    lr = lx % ly; break;
                                case "|": lr = lx | ly; break;
                                case "^": lr = lx ^ ly; break;
                                case "&": lr = lx & ly; break;
                                case "<<": lr = lx << (int)(ly & 31); break;
                                case ">>": lr = lx >> (int)(ly & 31); break;
                                default: throw new EvalException($"operator '{op}' is not modelled");
                            }
                            r = Norm(resultKind, lr);
                        }
                        break;
                }
                c[i] = r;
            }

            return new Val(resultKind, shape.Rows, shape.Cols, shape.IsMatrix, shape.IsScalar, c);
        }

        private Val Unary()
        {
            // Prefix operators and casts are collected in a loop (never recursed), so a long chain
            // of them cannot overflow the stack, then applied innermost first.
            var prefixes = new List<(string Op, NumType Cast)>();
            while (true)
            {
                if (Is("-") || Is("+") || Is("!") || Is("~"))
                {
                    prefixes.Add((Cur.Text, default));
                    _p++;
                    if (prefixes.Count > MaxPrefixChain) throw new EvalException("too many chained prefix operators");
                    continue;
                }

                // '(' type ')' unary   -- a cast
                if (Is("(") && _p + 2 < _end && _t[_p + 1].Kind == K.Ident &&
                    TryParseTypeName(_t[_p + 1].Text, out NumType castType) &&
                    _t[_p + 2].Kind == K.Punct && _t[_p + 2].Text == ")")
                {
                    prefixes.Add(("cast", castType));
                    _p += 3;
                    if (prefixes.Count > MaxPrefixChain) throw new EvalException("too many chained prefix operators");
                    continue;
                }
                break;
            }

            Val v = Postfix();
            for (int i = prefixes.Count - 1; i >= 0; i--)
            {
                (string op, NumType cast) = prefixes[i];
                switch (op)
                {
                    case "-": v = Negate(v); break;
                    case "+": break;
                    case "!":
                        v = v with { Kind = Kind.Bool, C = v.C.Select(x => x == 0 ? 1.0 : 0.0).ToArray() };
                        break;
                    case "~":
                        if (v.Kind == Kind.Float) throw new EvalException("'~' needs an integer operand");
                        {
                            Kind k = v.Kind == Kind.UInt ? Kind.UInt : Kind.Int;
                            v = v with { Kind = k, C = v.C.Select(x => Norm(k, ~(long)x)).ToArray() };
                        }
                        break;
                    default:
                        v = Coerce(v, cast);
                        break;
                }
            }
            return v;
        }

        private static Val Negate(Val v)
        {
            Kind k = v.Kind == Kind.Bool ? Kind.Int : v.Kind;
            return v with { Kind = k, C = v.C.Select(x => k == Kind.Float ? -x : Norm(k, -x)).ToArray() };
        }

        private Val Postfix()
        {
            Val v = Primary();
            while (true)
            {
                if (Is("."))
                {
                    _p++;
                    if (Cur.Kind != K.Ident) throw new EvalException("expected a swizzle after '.'");
                    string sw = Cur.Text;
                    _p++;
                    v = Swizzle(v, sw);
                    continue;
                }
                if (Is("["))
                {
                    int close = FindClose(_p);
                    var sub = new Evaluator(_t, _p + 1, close, _consts) { _depth = _depth + 1 };
                    Val idx = sub.EvalAll();
                    _p = close + 1;
                    if (idx.Count != 1) throw new EvalException("an array index is not a scalar");
                    int i = (int)Norm(Kind.Int, idx.C[0]);
                    if (v.IsMatrix)
                    {
                        if (i < 0 || i >= v.Rows) throw new EvalException("matrix row index out of range");
                        var row = new double[v.Cols];
                        Array.Copy(v.C, i * v.Cols, row, 0, v.Cols);
                        v = new Val(v.Kind, 1, v.Cols, false, v.Cols == 1, row);
                    }
                    else
                    {
                        if (i < 0 || i >= v.Count) throw new EvalException("vector index out of range");
                        v = Val.Scalar(v.Kind, v.C[i]);
                    }
                    continue;
                }
                return v;
            }
        }

        private int FindClose(int open)
        {
            int depth = 0;
            for (int i = open; i < _end; i++)
            {
                if (_t[i].Kind != K.Punct) continue;
                if (_t[i].Text == "[") depth++;
                else if (_t[i].Text == "]") { depth--; if (depth == 0) return i; }
            }
            throw new EvalException("unterminated '['");
        }

        private static Val Swizzle(Val v, string sw)
        {
            if (v.IsMatrix) throw new EvalException("a matrix swizzle is not modelled");
            string xyzw = "xyzw", rgba = "rgba";
            var c = new List<double>();
            foreach (char ch in sw)
            {
                int i = xyzw.IndexOf(ch);
                if (i < 0) i = rgba.IndexOf(ch);
                if (i < 0 || i >= v.Count) throw new EvalException($"'.{sw}' is not a valid swizzle of this value");
                c.Add(v.C[i]);
            }
            if (c.Count is < 1 or > 4) throw new EvalException($"'.{sw}' is not a valid swizzle");
            return c.Count == 1
                ? Val.Scalar(v.Kind, c[0])
                : new Val(v.Kind, 1, c.Count, false, false, c.ToArray());
        }

        private Val Primary()
        {
            if (AtEnd) throw new EvalException("the initializer ends where a value was expected");
            Tk t = Cur;

            if (t.Kind == K.Number)
            {
                _p++;
                return ParseNumber(t.Text);
            }

            if (Is("("))
            {
                _p++;
                Val inner = Expression();
                Expect(")");
                return inner;
            }

            if (t.Kind == K.Ident)
            {
                _p++;
                switch (t.Text)
                {
                    case "true": return Val.Scalar(Kind.Bool, 1);
                    case "false": return Val.Scalar(Kind.Bool, 0);
                }

                if (Is("("))
                {
                    var args = CallArguments();
                    if (TryParseTypeName(t.Text, out NumType ctor))
                        return Construct(ctor, args, t.Text);
                    if (TryTemplateConstructor(t.Text, out NumType tctor))
                        return Construct(tctor, args, t.Text);
                    return Intrinsic(t.Text, args);
                }

                if (_consts.TryGetValue(t.Text, out Val? known))
                    return known;

                throw new EvalException(
                    $"'{t.Text}' is not a literal, a const the reader has seen, or a supported call");
            }

            throw new EvalException($"unexpected '{t.Text}' in an initializer");
        }

        private static bool TryTemplateConstructor(string name, out NumType type)
        {
            type = default;
            return false;
        }

        private List<Val> CallArguments()
        {
            Expect("(");
            var args = new List<Val>();
            if (Take(")")) return args;
            while (true)
            {
                args.Add(Expression());
                if (Take(",")) continue;
                Expect(")");
                return args;
            }
        }

        private static Val Construct(NumType type, List<Val> args, string name)
        {
            var comps = new List<double>();
            foreach (Val a in args) comps.AddRange(a.C);
            if (comps.Count == 1 && type.Count > 1)
                return Splat(args[0], type);
            if (comps.Count != type.Count)
                throw new EvalException(
                    $"{name}(...) is given {comps.Count} component(s) but needs {type.Count}");
            return Val.OfType(type, comps.Select(x => Norm(type.Kind, x)).ToArray());
        }

        private static Val ParseNumber(string text)
        {
            string s = text;
            bool isHex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

            if (isHex)
            {
                string digits = s[2..].TrimEnd('u', 'U', 'l', 'L');
                if (!long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hv) || hv > uint.MaxValue)
                    throw new EvalException($"cannot read the number '{text}'");
                // A hex literal that does not fit an int is a uint.
                return hv > int.MaxValue || s.EndsWith('u') || s.EndsWith('U')
                    ? Val.Scalar(Kind.UInt, (uint)hv)
                    : Val.Scalar(Kind.Int, (int)hv);
            }

            bool isFloat = s.Contains('.') || s.Contains('e') || s.Contains('E');
            bool unsignedSuffix = false;
            string body = s;
            // Strip suffixes.
            while (body.Length > 0 && "fFhHuUlL".IndexOf(body[^1]) >= 0)
            {
                if (body[^1] is 'f' or 'F' or 'h' or 'H') isFloat = true;
                if (body[^1] is 'u' or 'U') unsignedSuffix = true;
                body = body[..^1];
            }
            if (isFloat)
            {
                if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    throw new EvalException($"cannot read the number '{text}'");
                return Val.Scalar(Kind.Float, (float)d);
            }

            long l;
            if (body.Length > 1 && body[0] == '0')
            {
                // C rules: a leading zero is octal.
                try { l = System.Convert.ToInt64(body, 8); }
                catch (Exception) { throw new EvalException($"cannot read the number '{text}' (not a valid octal literal)"); }
            }
            else if (!long.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out l))
                throw new EvalException($"cannot read the number '{text}'");

            if (l > uint.MaxValue) throw new EvalException($"the integer literal '{text}' does not fit 32 bits");
            // A decimal literal that does not fit an int is a uint.
            return unsignedSuffix || l > int.MaxValue ? Val.Scalar(Kind.UInt, (uint)l) : Val.Scalar(Kind.Int, (int)l);
        }

        // ---- math intrinsics --------------------------------------------------------------

        private static Val Intrinsic(string name, List<Val> a)
        {
            Func<float, float>? f1 = name switch
            {
                "abs" => MathF.Abs,
                "sqrt" => MathF.Sqrt,
                "rsqrt" => x => 1f / MathF.Sqrt(x),
                "rcp" => x => 1f / x,
                "sin" => MathF.Sin,
                "cos" => MathF.Cos,
                "tan" => MathF.Tan,
                "asin" => MathF.Asin,
                "acos" => MathF.Acos,
                "atan" => MathF.Atan,
                "exp" => MathF.Exp,
                "exp2" => x => MathF.Pow(2f, x),
                "log" => MathF.Log,
                "log2" => MathF.Log2,
                "log10" => MathF.Log10,
                "floor" => MathF.Floor,
                "ceil" => MathF.Ceiling,
                "trunc" => MathF.Truncate,
                "round" => x => MathF.Round(x, MidpointRounding.ToEven),
                "frac" => x => x - MathF.Floor(x),
                "saturate" => x => Math.Min(1f, Math.Max(0f, x)),
                "radians" => x => x * (MathF.PI / 180f),
                "degrees" => x => x * (180f / MathF.PI),
                "sign" => x => x > 0 ? 1f : x < 0 ? -1f : 0f,
                _ => null,
            };
            if (f1 is not null)
            {
                if (a.Count != 1) throw new EvalException($"{name}() takes one argument");
                Val v = a[0];
                if (name == "abs" && v.Kind != Kind.Float)
                    return v with { C = v.C.Select(x => Norm(v.Kind == Kind.UInt ? Kind.UInt : Kind.Int, Math.Abs(x))).ToArray(), Kind = v.Kind == Kind.UInt ? Kind.UInt : Kind.Int };
                Val fv = ConvertKind(v, Kind.Float);
                return fv with { C = fv.C.Select(x => (double)f1((float)x)).ToArray() };
            }

            switch (name)
            {
                case "min":
                case "max":
                {
                    if (a.Count != 2) throw new EvalException($"{name}() takes two arguments");
                    bool isMin = name == "min";
                    return Combine(a[0], a[1], name, (x, y) => isMin ? Math.Min(x, y) : Math.Max(x, y));
                }
                case "pow":
                    if (a.Count != 2) throw new EvalException("pow() takes two arguments");
                    return Combine(ConvertKind(a[0], Kind.Float), ConvertKind(a[1], Kind.Float), name, (x, y) => MathF.Pow((float)x, (float)y));
                case "fmod":
                    if (a.Count != 2) throw new EvalException("fmod() takes two arguments");
                    return Combine(ConvertKind(a[0], Kind.Float), ConvertKind(a[1], Kind.Float), name, (x, y) => (float)x % (float)y);
                case "atan2":
                    if (a.Count != 2) throw new EvalException("atan2() takes two arguments");
                    return Combine(ConvertKind(a[0], Kind.Float), ConvertKind(a[1], Kind.Float), name, (x, y) => MathF.Atan2((float)x, (float)y));
                case "clamp":
                {
                    if (a.Count != 3) throw new EvalException("clamp() takes three arguments");
                    Val lo = Combine(a[0], a[1], name, Math.Max);
                    return Combine(lo, a[2], name, Math.Min);
                }
                case "lerp":
                {
                    if (a.Count != 3) throw new EvalException("lerp() takes three arguments");
                    Val x = ConvertKind(a[0], Kind.Float), y = ConvertKind(a[1], Kind.Float), s = ConvertKind(a[2], Kind.Float);
                    Val diff = Apply("-", y, x);
                    Val scaled = Apply("*", diff, s);
                    return Apply("+", x, scaled);
                }
            }

            throw new EvalException($"the call to '{name}' is not one the initializer evaluator models");
        }

        private static Val Combine(Val a, Val b, string name, Func<double, double, double> f)
        {
            Val shape;
            if (a.Count == b.Count) shape = a;
            else if (a.Count == 1) shape = b;
            else if (b.Count == 1) shape = a;
            else throw new EvalException($"the arguments of {name}() have different shapes");

            Kind k = a.Kind == Kind.Float || b.Kind == Kind.Float ? Kind.Float : a.Kind == Kind.UInt || b.Kind == Kind.UInt ? Kind.UInt : Kind.Int;
            var c = new double[shape.Count];
            for (int i = 0; i < c.Length; i++)
                c[i] = Norm(k, f(a.Count == 1 ? a.C[0] : a.C[i], b.Count == 1 ? b.C[0] : b.C[i]));
            return new Val(k, shape.Rows, shape.Cols, shape.IsMatrix, shape.IsScalar, c);
        }
    }
}
