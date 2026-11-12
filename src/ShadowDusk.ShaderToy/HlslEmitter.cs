using System.Text;
using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// Translates the parsed GLSL subset AST into HLSL source text, honoring every translation trap:
/// type spelling (trap 1), matrix-multiply order (trap 2), sign-correct <c>mod</c> (trap 3),
/// intrinsic renames (trap 4), and stripped precision (trap 5, handled earlier in the preprocessor).
///
/// <para><b>Matrix trap (trap 2) — the proof.</b> GLSL is column-major and evaluates <c>M * v</c> as
/// "matrix times column vector". HLSL <c>mul(a, b)</c> with a row-vector <c>a</c> and matrix <c>b</c>
/// computes the row-major product, and crucially: feeding the <em>same</em> scalar list to an HLSL
/// <c>floatNxN(...)</c> constructor that a GLSL <c>matN(...)</c> constructor received yields the
/// <em>transpose</em> of the GLSL matrix (GLSL constructors fill column-major, HLSL row-major). The
/// standard, self-consistent port is therefore: emit matrix constructors with the identical scalar
/// list (producing Mᵀ), and translate GLSL <c>M * v</c> as HLSL <c>mul(v, M_hlsl)</c>. Because
/// <c>v · Mᵀ</c> (row-vector times the transpose, HLSL's mul) equals <c>M · v</c> (column-major),
/// the two transposes cancel and the result matches GLSL exactly. The canonical case is a 2D
/// rotation: GLSL <c>mat2(c,-s, s,c) * v</c> rotates by +θ; the emitter produces
/// <c>mul(v, float2x2(c,-s, s,c))</c>, which is <c>(c*vx - s*vy, s*vx + c*vy)</c> — the same +θ
/// rotation. (Verified by the <c>mat2</c> rotation unit test in the test project.)</para>
/// </summary>
internal sealed class HlslEmitter
{
    private readonly TypeInference _types;
    private readonly StringBuilder _sb = new();
    private int _indent;

    /// <summary>Set of uniform names actually referenced (for the harness to emit only those).</summary>
    public SortedSet<string> ReferencedUniforms { get; } = new(StringComparer.Ordinal);

    /// <summary>True once any expression used the sign-correct <c>mod</c> path (so the helper is emitted).</summary>
    public bool UsedGlslMod { get; private set; }

    /// <summary>True once the body referenced the built-in <c>gl_FragCoord</c> (G3c). In ShaderToy mode
    /// the harness then publishes <c>gl_FragCoord</c> as a <c>static float4</c> set before calling
    /// <c>mainImage</c>; in plain-GLSL mode the static is always emitted.</summary>
    public bool UsedGlFragCoord { get; private set; }

    /// <summary>True once the body referenced a screen-coordinate alias (an ignored stage-I/O coordinate
    /// varying, or the OpenFL <c>openfl_TextureCoordv</c>). The harness then publishes the normalized
    /// screen UV as a <c>static float2 sd_ScreenUV;</c> set before the entry runs.</summary>
    public bool UsedScreenUv { get; private set; }

    public HlslEmitter(TypeInference types) => _types = types;

    private void Line(string text)
    {
        _sb.Append(' ', _indent * 4);
        _sb.Append(text);
        _sb.Append('\n');
    }

    private void RawLine(string text)
    {
        _sb.Append(text);
        _sb.Append('\n');
    }

    // ── top-level emission ────────────────────────────────────────────────────

    /// <summary>Emit a user function as HLSL into the running buffer.</summary>
    public string EmitFunction(FunctionDecl fn)
    {
        _sb.Clear();
        _indent = 0;

        string ret = HlslType(fn.ReturnType);
        var ps = new List<string>();
        _types.PushScope();
        foreach (ParamDecl p in fn.Parameters)
        {
            string qual = p.Qualifier switch
            {
                ParamQualifier.Out => "out ",
                ParamQualifier.InOut => "inout ",
                _ => string.Empty,
            };
            // F1: a param renamed for HLSL safety emits its safe name; type inference stays on the
            // ORIGINAL name (the AST is unchanged), so EmitIdentifier maps value-refs at emit time.
            string pname = MapLocal(p.Name);
            if (p.ArraySize is { } n)
            {
                // G7c: an array parameter. HLSL spells the size on the declarator name: `T name[N]`.
                ps.Add($"{qual}{HlslType(p.TypeName)} {pname}[{n}]");
                _types.DeclareArray(p.Name, p.TypeName);
            }
            else
            {
                ps.Add($"{qual}{HlslType(p.TypeName)} {pname}");
                _types.Declare(p.Name, p.TypeName);
            }
        }

        Line($"{ret} {MapGlobal(fn.Name)}({string.Join(", ", ps)})");
        EmitBlock(fn.Body);
        _types.PopScope();
        return _sb.ToString();
    }

    /// <summary>Emit a top-level <c>const</c> global (including a <c>const</c> array, G7).</summary>
    public string EmitGlobalConst(GlobalConstDecl g)
    {
        _sb.Clear();
        _indent = 0;
        string type = HlslType(g.TypeName);
        string name = MapGlobal(g.Name);
        if (g.ArraySize is { } n)
        {
            // const float k[3] = float[](a,b,c)  ->  static const float k[3] = { a, b, c };
            Line($"static const {type} {name}[{n}] = {EmitExpr(g.Initializer)};");
        }
        else
        {
            Line($"static const {type} {name} = {EmitExpr(g.Initializer)};");
        }

        return _sb.ToString();
    }

    /// <summary>
    /// Emit a top-level non-<c>const</c> mutable global (G1) as an HLSL <c>static</c> global, with the
    /// matching per-invocation-mutable semantics of a GLSL fragment-scope global. An initializer (if
    /// any) is truncated-to-width like a local declaration so a wider value narrows explicitly.
    /// </summary>
    public string EmitGlobalVar(GlobalVarDecl g)
    {
        _sb.Clear();
        _indent = 0;
        string type = HlslType(g.TypeName);
        string name = MapGlobal(g.Name);
        if (g.ArraySize is { } n)
        {
            // A non-const array global (G7): `static float k[3] [= { ... }];`.
            Line(g.Initializer is null
                ? $"static {type} {name}[{n}];"
                : $"static {type} {name}[{n}] = {EmitExpr(g.Initializer)};");
        }
        else if (g.Initializer is null)
        {
            Line($"static {type} {name};");
        }
        else
        {
            Line($"static {type} {name} = {EmitInitializer(g.TypeName, g.Initializer)};");
        }

        return _sb.ToString();
    }

    /// <summary>
    /// Translate a custom-uniform default initializer (G4) to an HLSL expression string, narrowing a
    /// wider value to the declared width if needed. Used by the harness to emit
    /// <c>&lt;type&gt; &lt;name&gt; = &lt;default&gt;;</c>.
    /// </summary>
    public string EmitUniformDefault(string declaredGlslType, Expr value)
    {
        _sb.Clear();
        _indent = 0;
        return EmitInitializer(declaredGlslType, value);
    }

    // ── statements ─────────────────────────────────────────────────────────────

    private void EmitBlock(BlockStmt block)
    {
        Line("{");
        _indent++;
        // A brace block is a real scope in BOTH languages: push an inference scope so a nested-block
        // local that shadows an outer variable stops poisoning inference once the block closes (the
        // outer declaration wins again, e.g. a vec2 shadowed by a mat2 must not turn a later `*`
        // into mul()).
        _types.PushScope();
        foreach (Stmt s in block.Statements)
        {
            EmitStatement(s);
        }

        _types.PopScope();
        _indent--;
        Line("}");
    }

    private void EmitStatement(Stmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt b:
                EmitBlock(b);
                break;
            case VarDeclStmt v:
                EmitVarDecl(v);
                break;
            case MultiDeclStmt m:
                foreach (VarDeclStmt d in m.Declarators)
                {
                    EmitVarDecl(d);
                }

                break;
            case ExprStmt e:
                Line($"{EmitStatementExpr(e.Expression)};");
                break;
            case IfStmt i:
                EmitIf(i);
                break;
            case SwitchStmt sw:
                EmitSwitch(sw);
                break;
            case ForStmt f:
                EmitFor(f);
                break;
            case WhileStmt w:
                Line($"while ({EmitCondition(w.Condition)})");
                EmitBody(w.Body);
                break;
            case DoWhileStmt d:
                Line("do");
                EmitBody(d.Body);
                Line($"while ({EmitCondition(d.Condition)});");
                break;
            case ReturnStmt r:
                Line(r.Value is null ? "return;" : $"return {EmitExpr(r.Value)};");
                break;
            case BreakStmt:
                Line("break;");
                break;
            case ContinueStmt:
                Line("continue;");
                break;
            case DiscardStmt:
                Line("discard;");
                break;
            default:
                throw new ConvertException(
                    "Internal: unhandled statement.", stmt.Line, stmt.Column);
        }
    }

    private void EmitVarDecl(VarDeclStmt v)
    {
        string type = HlslType(v.TypeName);
        string prefix = v.IsConst ? "const " : string.Empty;
        string name = MapLocal(v.Name); // F1: emit the safe name; type inference stays on the original

        if (v.ArraySize is { } n)
        {
            // G7: a local fixed-size array (`float arr[4];` / `const float k[3] = float[](...);`).
            _types.DeclareArray(v.Name, v.TypeName);
            Line(v.Initializer is null
                ? $"{prefix}{type} {name}[{n}];"
                : $"{prefix}{type} {name}[{n}] = {EmitExpr(v.Initializer)};");
            return;
        }

        _types.Declare(v.Name, v.TypeName);
        if (v.Initializer is null)
        {
            Line($"{prefix}{type} {name};");
        }
        else
        {
            Line($"{prefix}{type} {name} = {EmitInitializer(v.TypeName, v.Initializer)};");
        }
    }

    /// <summary>
    /// Emit an initializer / assigned value (B4): when the declared/target type is a narrower vector
    /// than the value's inferred type, GLSL silently truncates but stricter HLSL errors
    /// (<c>-Werror,-Wconversion</c>). Insert an explicit truncating swizzle (<c>.xy</c>/<c>.xyz</c>)
    /// so the conversion is explicit. Equal/compatible widths and scalars are emitted unchanged.
    /// </summary>
    private string EmitInitializer(string declaredGlslType, Expr value)
    {
        GlslType declared = TypeTable.Resolve(declaredGlslType);
        return TruncateToWidth(declared, value);
    }

    /// <summary>
    /// If <paramref name="target"/> is a vector strictly narrower than the value's inferred vector
    /// width, append a truncating swizzle to the emitted value; otherwise emit it unchanged.
    /// </summary>
    private string TruncateToWidth(GlslType target, Expr value)
    {
        Emitted e = Emit(value);
        string emitted = e.Text;
        if (!target.IsVector)
        {
            return emitted;
        }

        GlslType valueType = _types.Infer(value);
        if (!valueType.IsVector || valueType.Rows <= target.Rows)
        {
            return emitted;
        }

        string swizzle = target.Rows switch
        {
            2 => "xy",
            3 => "xyz",
            _ => string.Empty,
        };
        if (swizzle.Length == 0)
        {
            return emitted;
        }

        // Parenthesize (when looser than postfix) so the swizzle binds to the whole value, then select
        // the leading components.
        return $"{Wrap(e, PrecPostfix)}.{swizzle}";
    }

    private void EmitIf(IfStmt i)
    {
        Line($"if ({EmitCondition(i.Condition)})");
        EmitBody(i.Then);
        if (i.Else is not null)
        {
            Line("else");
            EmitBody(i.Else);
        }
    }

    /// <summary>
    /// Lower a <c>switch (selector) { case K: ...; default: ... }</c> to an if / else-if / else chain
    /// (HLSL on the SM3 / FNA targets has no native <c>switch</c>). The selector is evaluated exactly
    /// ONCE into a fresh local (so a non-pure selector is not re-evaluated per arm), then each non-default
    /// arm becomes <c>if/else if (sd_sw == label || ...)</c> and the <c>default</c> arm becomes the final
    /// <c>else</c>. Stacked <c>case</c> labels sharing one body become an OR'd condition. The trailing
    /// <c>break;</c> of each arm was already stripped by the parser (a <c>break</c> outside a loop is
    /// illegal HLSL); a <c>return;</c> inside an arm is preserved and still exits the function.
    /// </summary>
    private void EmitSwitch(SwitchStmt sw)
    {
        string selType = HlslType(InferSelectorTypeName(sw.Selector));
        string sel = $"sd_sw{_switchCounter++}";
        Line($"{selType} {sel} = {EmitExpr(sw.Selector)};");

        // Reorder so the default arm (if any) is emitted last as the final `else`, regardless of its
        // source position. Equality semantics are independent of arm order once break/return terminate
        // each arm (no fall-through reached this far — the parser rejected it).
        var valueCases = sw.Cases.Where(c => !c.IsDefault).ToList();
        SwitchCase? defaultCase = sw.Cases.FirstOrDefault(c => c.IsDefault);

        bool first = true;
        foreach (SwitchCase c in valueCases)
        {
            // A label group with an empty body and shared labels still emits its condition; the body is
            // simply empty. Build `sel == L0 || sel == L1 ...`.
            string cond = string.Join(
                " || ", c.Labels.Select(l => $"{sel} == {EmitAtLeast(l, PrecRelational)}"));
            Line($"{(first ? "if" : "else if")} ({cond})");
            EmitCaseBody(c.Body);
            first = false;
        }

        if (defaultCase is not null)
        {
            if (first)
            {
                // Only a default arm: emit its body unconditionally in a block.
                EmitCaseBody(defaultCase.Body);
            }
            else
            {
                Line("else");
                EmitCaseBody(defaultCase.Body);
            }
        }
    }

    /// <summary>Best-effort HLSL type spelling for a switch selector's local temp. A non-inferrable
    /// selector defaults to <c>int</c> (the GLSL switch selector is an integer expression).</summary>
    private string InferSelectorTypeName(Expr selector)
    {
        GlslType t = _types.Infer(selector);
        if (t.IsKnown && !t.IsVector && !t.IsMatrix)
        {
            return t.Scalar switch
            {
                ScalarKind.Bool => "bool",
                ScalarKind.Float => "float",
                ScalarKind.Uint => "uint",
                _ => "int",
            };
        }

        return "int";
    }

    private void EmitCaseBody(IReadOnlyList<Stmt> body)
    {
        Line("{");
        _indent++;
        // The synthesized braces are a real HLSL scope; mirror them in inference so a declaration
        // inside one lowered arm does not leak into inference for the code after the switch.
        _types.PushScope();
        foreach (Stmt s in body)
        {
            EmitStatement(s);
        }

        _types.PopScope();
        _indent--;
        Line("}");
    }

    private int _switchCounter;

    private void EmitFor(ForStmt f)
    {
        // Legacy HLSL for-scope leak: if this loop reuses an init-variable name an earlier loop already
        // used, IdentifierSafety planned a rename for it. Activate it for the whole loop (init + condition +
        // increment + body) so the declaration and every reference inside emit the safe name, then pop it so
        // it does not leak to following statements.
        bool scoped = _forLoopRenames.TryGetValue(f, out Dictionary<string, string>? loopScope);
        if (scoped)
        {
            _loopRenameScopes.Add(loopScope!);
        }

        // Render init / cond / inc inline. A var-decl init is rendered without trailing newline.
        string init = f.Init switch
        {
            null => string.Empty,
            VarDeclStmt vd => RenderInlineVarDecl(vd),
            MultiDeclStmt md => RenderInlineMultiDecl(md),
            ExprStmt es => EmitStatementExpr(es.Expression),
            _ => string.Empty,
        };
        // A for-condition is a boolean context exactly like if/while/do…while (B2/B3): route it
        // through EmitCondition so a top-level equality is not wrapped in extraneous parens
        // (`for (…; (i != n); …)` trips -Werror,-Wparentheses-equality) and a vector compare is
        // scalarized with all()/any().
        string cond = f.Condition is null ? string.Empty : EmitCondition(f.Condition);
        string inc = f.Increment is null ? string.Empty : EmitStatementExpr(f.Increment);
        Line($"for ({init}; {cond}; {inc})");
        EmitBody(f.Body);

        if (scoped)
        {
            _loopRenameScopes.RemoveAt(_loopRenameScopes.Count - 1);
        }
    }

    private string RenderInlineVarDecl(VarDeclStmt v)
    {
        _types.Declare(v.Name, v.TypeName);
        string type = HlslType(v.TypeName);
        string name = MapLocal(v.Name);
        return v.Initializer is null
            ? $"{type} {name}"
            : $"{type} {name} = {EmitInitializer(v.TypeName, v.Initializer)}";
    }

    private string RenderInlineMultiDecl(MultiDeclStmt m)
    {
        // GLSL `for (int i = 0, n = 4; ...)` -> HLSL `int i = 0, n = 4`.
        string type = HlslType(m.Declarators[0].TypeName);
        var parts = new List<string>();
        foreach (VarDeclStmt d in m.Declarators)
        {
            _types.Declare(d.Name, d.TypeName);
            string name = MapLocal(d.Name);
            parts.Add(d.Initializer is null ? name : $"{name} = {EmitInitializer(d.TypeName, d.Initializer)}");
        }

        return $"{type} {string.Join(", ", parts)}";
    }

    /// <summary>Emit a loop/branch body, wrapping a single statement in braces for safety.</summary>
    private void EmitBody(Stmt body)
    {
        if (body is BlockStmt b)
        {
            EmitBlock(b);
        }
        else
        {
            Line("{");
            _indent++;
            // The synthesized braces are a real HLSL scope; mirror them in inference (same reason
            // as EmitBlock) so a declaration here does not leak past the wrapped statement.
            _types.PushScope();
            EmitStatement(body);
            _types.PopScope();
            _indent--;
            Line("}");
        }
    }

    // ── expressions ────────────────────────────────────────────────────────────
    //
    // Parenthesization policy (Phase 71, readability with no change in meaning). The parser drops the
    // source's grouping parens (`( … )` yields the inner Expr; there is no ParenExpr node), so the
    // emitter decides every paren. Each emitted expression carries the C/HLSL precedence level of its
    // OUTERMOST operator (Emitted.Prec), and a parent wraps a child only when one of these holds:
    //   1. Precedence: the child binds looser than the slot it sits in (`(a + b) * c`).
    //   2. Associativity: the right operand of a binary operator at the SAME level (`a - (b - c)`,
    //      `a / (b * c)`, and also `a + (b + c)`, since float addition is not associative).
    //   3. Clarity: a few pairings that precedence leaves unambiguous but that readers misread, the same
    //      set clang flags under -Wparentheses (see NeedsClarityParens).
    //   4. Assignment and comma always wrap themselves outside statement position, and a conditional
    //      nested inside another conditional is wrapped.
    // The precedence the emitter reasons about is that of the EMITTED HLSL, not the GLSL AST: a GLSL
    // matrix `*` becomes the call `mul(b, a)`, a vector `==` becomes `all(a == b)`, and those are
    // atoms to their parent.

    // HLSL/C precedence levels, lowest binding first.
    private const int PrecSequence = 0;
    private const int PrecAssign = 1;
    private const int PrecConditional = 2;
    private const int PrecLogicalOr = 3;
    private const int PrecLogicalAnd = 4;
    private const int PrecBitOr = 5;
    private const int PrecBitXor = 6;
    private const int PrecBitAnd = 7;
    private const int PrecEquality = 8;
    private const int PrecRelational = 9;
    private const int PrecShift = 10;
    private const int PrecAdditive = 11;
    private const int PrecMultiplicative = 12;
    private const int PrecUnary = 13;
    private const int PrecPostfix = 14;
    private const int PrecPrimary = 15;

    /// <summary>An emitted HLSL expression with the precedence of its outermost operator, and that
    /// operator's spelling when it is a binary operator (used by the clarity rules).</summary>
    private readonly record struct Emitted(string Text, int Prec, string? BinaryOp = null);

    private static Emitted Atom(string text) => new(text, PrecPrimary);

    private static Emitted Postfix(string text) => new(text, PrecPostfix);

    private static int BinaryPrecedence(string op) => op switch
    {
        "||" => PrecLogicalOr,
        "&&" => PrecLogicalAnd,
        "|" => PrecBitOr,
        "^" => PrecBitXor,
        "&" => PrecBitAnd,
        "==" or "!=" => PrecEquality,
        "<" or ">" or "<=" or ">=" => PrecRelational,
        "<<" or ">>" => PrecShift,
        "+" or "-" => PrecAdditive,
        "*" or "/" or "%" => PrecMultiplicative,
        _ => throw new InvalidOperationException($"Internal: unknown binary operator '{op}'."),
    };

    /// <summary>Translate an expression to an HLSL string, with only the parentheses its meaning or a
    /// reader needs (see the policy above).</summary>
    public string EmitExpr(Expr expr) => Emit(expr).Text;

    private Emitted Emit(Expr expr) => expr switch
    {
        IntLiteralExpr i => Atom(i.Text),
        FloatLiteralExpr f => Atom(NormalizeFloat(f.Text)),
        BoolLiteralExpr b => Atom(b.Value ? "true" : "false"),
        // An identifier can resolve to a member path (openfl_TextureSize -> iResolution.xy): still
        // postfix-tight, so it never needs wrapping.
        IdentifierExpr id => Postfix(EmitIdentifier(id)),
        SwizzleExpr sw => Postfix(EmitMemberOrSwizzle(sw)),
        IndexExpr idx => Postfix(EmitIndex(idx)),
        CallExpr call => EmitCall(call),
        ArrayConstructorExpr ac => Atom(EmitArrayConstructor(ac)),
        BraceInitExpr bi => Atom($"{{ {string.Join(", ", bi.Elements.Select(EmitExpr))} }}"),
        UnaryExpr un => EmitUnary(un),
        BinaryExpr bin => EmitBinary(bin),
        ConditionalExpr c => new Emitted(
            $"{ConditionalOperand(c.Condition)} ? {ConditionalOperand(c.WhenTrue)} : {ConditionalOperand(c.WhenFalse)}",
            PrecConditional),
        // Assignment and comma are the two LOWEST-precedence operators in both GLSL and HLSL, so in
        // sub-expression position they must re-parenthesize themselves or the surrounding operator
        // silently binds tighter than it did in the GLSL: the raymarching idiom
        // `if ((d = map(p)) < 0.001)` would emit `if (d = map(p) < 0.001)`, assigning a bool 0/1 to d
        // instead of the distance — valid HLSL, wrong image, no diagnostic. They always wrap (even as a
        // call argument, where the grammar would allow a bare assignment) so the side effect stands out.
        // EmitStatementExpr keeps the statement-level spelling bare.
        SequenceExpr seq => Atom($"({string.Join(", ", seq.Items.Select(EmitExpr))})"),
        AssignExpr a => Atom($"({EmitAssign(a)})"),
        _ => throw new ConvertException("Internal: unhandled expression.", expr.Line, expr.Column),
    };

    /// <summary>Emit <paramref name="expr"/> for a slot that needs at least <paramref name="minPrec"/>,
    /// wrapping it in parentheses when its outermost operator binds looser (rule 1).</summary>
    private string EmitAtLeast(Expr expr, int minPrec) => Wrap(Emit(expr), minPrec);

    private static string Wrap(Emitted e, int minPrec) => e.Prec < minPrec ? $"({e.Text})" : e.Text;

    /// <summary>A conditional's condition and branches: anything looser than <c>||</c> wraps, which is
    /// exactly a nested conditional (rule 4; assignment and comma already wrap themselves).</summary>
    private string ConditionalOperand(Expr expr) => EmitAtLeast(expr, PrecLogicalOr);

    /// <summary>
    /// Emits an expression in STATEMENT position (an expression statement, or a
    /// <c>for</c> header's init/increment slot), where an assignment or comma sequence
    /// is already unambiguous and the parens <see cref="EmitExpr"/> adds would
    /// only be noise.
    /// </summary>
    private string EmitStatementExpr(Expr expr) => expr switch
    {
        AssignExpr a => EmitAssign(a),
        SequenceExpr seq => string.Join(", ", seq.Items.Select(EmitExpr)),
        _ => EmitExpr(expr),
    };

    /// <summary>
    /// Emit an assignment, with one trap (B1): a compound <c>*=</c> whose right-hand side is a matrix
    /// must honor the same matrix-multiply reordering as a binary <c>*</c>. GLSL <c>v *= M</c> means
    /// <c>v = v*M</c> (a row-vector times matrix); under the converter's <c>A*B → mul(B,A)</c> rule that
    /// is <c>v = mul(M, v)</c>. A plain <c>v *= M</c> would emit invalid HLSL (<c>float2 *= float2x2</c>).
    /// Scalar/vector <c>*=</c> (and every other compound op) stays component-wise and passes through
    /// unchanged.
    /// </summary>
    private string EmitAssign(AssignExpr a)
    {
        string target = EmitAtLeast(a.Target, PrecUnary);
        if (a.Op == "*=")
        {
            GlslType targetType = _types.Infer(a.Target);
            GlslType valueType = _types.Infer(a.Value);
            if (targetType.IsMatrix || valueType.IsMatrix)
            {
                // Desugar `lhs *= rhs` to `lhs = lhs * rhs` preserving GLSL operand order, and route
                // the multiply through the binary path so the matrix-order trap applies consistently.
                // GLSL `v *= M` means `v = v*M` (row-vector); EmitBinary turns `lhs * rhs` into
                // `mul(rhs, lhs)`, yielding `v = mul(M, v)`. (Inverting the order here would emit the
                // transpose, i.e. a vertical mirror.) For `A *= B` this is `A = A*B → mul(B, A)`.
                var product = new BinaryExpr
                {
                    Op = "*",
                    Left = a.Target,
                    Right = a.Value,
                    Line = a.Line,
                    Column = a.Column,
                };
                return $"{target} = {EmitBinary(product).Text}";
            }
        }

        // B4: a plain assignment whose RHS is a wider vector than the LHS truncates implicitly in
        // GLSL but errors under stricter HLSL; make the truncation explicit with a swizzle.
        if (a.Op == "=")
        {
            GlslType targetType = _types.Infer(a.Target);
            return $"{target} = {TruncateToWidth(targetType, a.Value)}";
        }

        return $"{target} {a.Op} {EmitExpr(a.Value)}";
    }

    /// <summary>
    /// Emit an expression used in a BOOLEAN context (an <c>if</c>/<c>while</c>/<c>do…while</c>/<c>for</c>
    /// condition). The two traps of that context are now handled by the general expression path:
    /// <list type="bullet">
    /// <item><b>B2 — over-parenthesization.</b> The call site already wraps the condition in
    /// <c>(...)</c>, and a top-level binary carries no outer parens of its own, so <c>if (a == 0.0)</c>
    /// never becomes <c>if ((a == 0.0))</c>, which fxc rejects under
    /// <c>-Werror,-Wparentheses-equality</c>.</item>
    /// <item><b>B3 — vector equality.</b> <see cref="EmitBinary"/> reduces a vector <c>==</c>/<c>!=</c>
    /// with <c>all</c>/<c>any</c> at every depth, so a comparison nested under <c>&amp;&amp;</c>,
    /// <c>||</c> or <c>!</c> is scalarized too.</item>
    /// </list>
    /// </summary>
    private string EmitCondition(Expr expr) => EmitExpr(expr);


    private string EmitIdentifier(IdentifierExpr id)
    {
        // F1: a local renamed for HLSL safety is always a local VALUE reference here (a renamed local
        // shadows every other meaning of the name in its scope; a call head goes through EmitCall, not
        // here). Emit its safe name directly. This also resolves an active for-loop variable rename (the
        // legacy for-scope leak). Empty maps for a shader with no collisions.
        if (TryRenameLocal(id.Name, out string renamedLocal))
        {
            return renamedLocal;
        }

        // A glslViewer alias (e.g. u_time) resolves to the ShaderToy built-in it was folded onto, so it
        // emits as that built-in's global and is tracked as a referenced built-in.
        string resolved = _types.ResolveName(id.Name);
        if (UniformInfo.IsUniform(resolved))
        {
            ReferencedUniforms.Add(resolved);
            return resolved;
        }

        if (_customUniforms.Contains(id.Name))
        {
            // A custom uniform: emitted verbatim (the harness declares it as an effect parameter).
            return id.Name;
        }

        // G3c: gl_FragCoord is a built-in usable anywhere in the body (mainImage or main). It aliases
        // the harness pixel coordinate as a float4 (.xy = fragCoord with the bottom-left Y convention,
        // .z = 0, .w = 1). Mark it used so the harness publishes the matching `static float4` global.
        if (id.Name == "gl_FragCoord")
        {
            UsedGlFragCoord = true;
            return "gl_FragCoord";
        }

        // Screen-coordinate alias (an ignored stage-I/O coordinate varying like vUv/texCoord/uv, or the
        // OpenFL fullscreen-filter coordinate openfl_TextureCoordv): resolves to the harness normalized
        // screen UV (fragCoord / iResolution.xy, [0,1], ShaderToy bottom-left origin). Rewrite the
        // reference to the harness static and mark it used so the harness publishes + sets it.
        if (_screenUvAliases.Contains(id.Name))
        {
            UsedScreenUv = true;
            ReferencedUniforms.Add("iResolution");
            return "sd_ScreenUV";
        }

        // OpenFL fullscreen-filter resolution global: openfl_TextureSize (vec2) resolves to the ShaderToy
        // iResolution.xy. Rewrite the reference and mark iResolution referenced.
        if (id.Name == "openfl_TextureSize")
        {
            ReferencedUniforms.Add("iResolution");
            return "iResolution.xy";
        }

        if (!_types.IsKnownIdentifier(id.Name) && !_userFunctions.Contains(id.Name))
        {
            // A known GLSL stage built-in that the single-pass 2D harness has no value for: name it
            // precisely (rather than a generic "undeclared identifier") so the boundary is clear.
            if (KnownUnsupportedGlBuiltins.TryGetValue(id.Name, out string? glReason))
            {
                throw new ConvertException(glReason, id.Line, id.Column, id.Name);
            }

            // A free (undeclared) identifier: not a local/param/const-global, not a ShaderToy
            // uniform, not a user function. Reject loudly at convert time rather than leaking it to
            // HLSL where it surfaces as "use of undeclared identifier". (Custom uniforms, ISF
            // builtins like RENDERSIZE, host-specific globals like iCurrentCursor, etc. land here —
            // we cannot invent a host-provided value.)
            throw new ConvertException(
                $"Undeclared identifier '{id.Name}' (not a ShaderToy built-in or declared uniform). " +
                "This shader depends on a host-provided value: '" + id.Name + "' is not a local " +
                "variable, a 'const' global, a user function, a declared custom uniform, or a predefined " +
                "ShaderToy uniform (iTime, iResolution, iMouse, iChannelN, ...). If it is a value your " +
                "host supplies, declare it as a top-level 'uniform' to expose it as an effect parameter.",
                id.Line, id.Column, id.Name);
        }

        // F1: a top-level user identifier (a const/mutable global, or a function used as a value) whose
        // name is an HLSL reserved keyword emits its safe renamed form. No-op for a clean name.
        return MapGlobal(id.Name);
    }

    /// <summary>
    /// Emit a <c>.member</c> access: a struct member (G6) is emitted verbatim (no swizzle translation,
    /// so a member whose name happens to be stpq-only is not mangled), while a vector component
    /// selection goes through the swizzle normalizer (stpq → xyzw).
    /// </summary>
    private string EmitMemberOrSwizzle(SwizzleExpr sw)
    {
        string target = EmitAtLeast(sw.Target, PrecPostfix);
        GlslType targetType = _types.Infer(sw.Target);
        return targetType.IsStruct
            ? $"{target}.{sw.Member}"
            : $"{target}.{TranslateSwizzle(sw.Member)}";
    }

    private string EmitIndex(IndexExpr idx)
    {
        // Track the array uniforms (iChannelTime / iChannelResolution) when indexed.
        if (idx.Target is IdentifierExpr id && UniformInfo.IsUniform(id.Name))
        {
            ReferencedUniforms.Add(id.Name);
        }

        return $"{EmitAtLeast(idx.Target, PrecPostfix)}[{EmitExpr(idx.Index)}]";
    }

    private Emitted EmitUnary(UnaryExpr un)
    {
        if (un.IsPostfix)
        {
            return Postfix($"{EmitAtLeast(un.Operand, PrecPostfix)}{un.Op}");
        }

        // A prefix operator applies to a unary-or-tighter operand, so `-(a + b)` keeps its parens and
        // `-x` does not. One extra case: an operand that itself starts with the same sign character
        // would fuse into a different token (`-` + `-x` is `--x`, a decrement), so it wraps.
        string operand = EmitAtLeast(un.Operand, PrecUnary);
        char last = un.Op[^1];
        if ((last == '-' || last == '+') && operand.Length > 0 && operand[0] == last)
        {
            operand = $"({operand})";
        }

        return new Emitted($"{un.Op}{operand}", PrecUnary);
    }

    /// <summary>
    /// B3 — vector equality reduction, used by <see cref="EmitBinary"/> in EVERY context. GLSL
    /// <c>vecA == vecB</c> yields a SINGLE bool (all components equal) in conditions, assignments,
    /// initializers, returns and call arguments alike, but HLSL <c>==</c> on vectors yields a
    /// bool-vector, so it must be reduced with <c>all(a == b)</c> (and <c>!=</c> with
    /// <c>any(a != b)</c>). Returns null when neither operand infers as a vector: scalar compares, and
    /// operands whose types are unknown, emit unchanged (the conservative pass-through).
    /// </summary>
    private Emitted? TryReduceVectorEquality(BinaryExpr bin)
    {
        if (bin.Op is not ("==" or "!="))
        {
            return null;
        }

        GlslType lt = _types.Infer(bin.Left);
        GlslType rt = _types.Infer(bin.Right);
        if (!lt.IsVector && !rt.IsVector)
        {
            return null;
        }

        string reducer = bin.Op == "==" ? "all" : "any";
        return Postfix($"{reducer}({InfixText(bin.Op, bin.Left, bin.Right)})");
    }

    private Emitted EmitBinary(BinaryExpr bin)
    {
        // B3 applies outside condition contexts too (a GLSL vector ==/!= is a single bool in an
        // assignment / initializer / return / call argument as well); reduce via the shared helper.
        if (TryReduceVectorEquality(bin) is { } reduced)
        {
            return reduced;
        }

        if (bin.Op == "*")
        {
            GlslType lt = _types.Infer(bin.Left);
            GlslType rt = _types.Infer(bin.Right);

            // Matrix multiply (trap 2). GLSL A * B  →  HLSL mul(B, A) preserves column-major semantics
            // given that matrix constructors are emitted with the identical (now transposed) scalar list.
            // A scalar operand is NOT a matrix multiply — it is component scaling and stays as `*`
            // (the generic path below).
            if ((lt.IsMatrix || rt.IsMatrix) && !lt.IsScalar && !rt.IsScalar)
            {
                return Postfix($"mul({EmitExpr(bin.Right)}, {EmitExpr(bin.Left)})");
            }
        }

        return new Emitted(InfixText(bin.Op, bin.Left, bin.Right), BinaryPrecedence(bin.Op), bin.Op);
    }

    /// <summary>
    /// A C-style cast <c>(floatN)x</c>. Its operand keeps parens unless it is postfix-tight, so a cast of
    /// a binary or unary expression reads <c>(float3)(a + b)</c> and a cast of a name reads <c>(float3)x</c>.
    /// </summary>
    private static Emitted Cast(string hlslType, Emitted operand) =>
        new($"({hlslType}){Wrap(operand, PrecPostfix)}", PrecUnary);

    /// <summary>Spell <c>left op right</c> with each operand wrapped only as the policy requires.</summary>
    private string InfixText(string op, Expr left, Expr right) =>
        $"{BinaryOperand(op, left, isRight: false)} {op} {BinaryOperand(op, right, isRight: true)}";

    private string BinaryOperand(string parentOp, Expr child, bool isRight) =>
        WrapOperand(parentOp, Emit(child), isRight);

    private static string WrapOperand(string parentOp, Emitted e, bool isRight)
    {
        int parentPrec = BinaryPrecedence(parentOp);
        bool wrap = e.Prec < parentPrec // rule 1: precedence
            || (isRight && e.Prec == parentPrec) // rule 2: left associativity
            || NeedsClarityParens(parentOp, e.BinaryOp); // rule 3: clarity
        return wrap ? $"({e.Text})" : e.Text;
    }

    /// <summary>
    /// Rule 3: pairings that precedence leaves unambiguous but that readers misread, so a paren is kept
    /// anyway. The set is the one clang flags under <c>-Wparentheses</c>:
    /// <list type="bullet">
    /// <item><c>-Wlogical-op-parentheses</c>: <c>&amp;&amp;</c> inside <c>||</c> (<c>a || (b &amp;&amp; c)</c>).</item>
    /// <item><c>-Wbitwise-op-parentheses</c> and its relatives: any other binary operator inside a
    /// bitwise <c>&amp;</c>/<c>|</c>/<c>^</c> (<c>a | (b &amp; c)</c>, <c>(a &amp; b) == c</c> is
    /// already required by precedence, <c>a &amp; (b + c)</c> is clarity).</item>
    /// <item><c>-Wshift-op-parentheses</c>: any other binary operator inside a shift, and a shift
    /// inside any other binary operator (<c>a &lt;&lt; (b + c)</c>, <c>(a &lt;&lt; b) &lt; c</c>).</item>
    /// <item>Comparison chains: a comparison inside a comparison (<c>(a &lt; b) == c</c>).</item>
    /// </list>
    /// A same-operator chain such as <c>a &amp;&amp; b &amp;&amp; c</c> or <c>a | b | c</c> stays bare.
    /// </summary>
    private static bool NeedsClarityParens(string parentOp, string? childOp)
    {
        if (childOp is null || childOp == parentOp)
        {
            return false;
        }

        static bool IsLogical(string op) => op is "&&" or "||";
        static bool IsBitwise(string op) => op is "&" or "|" or "^";
        static bool IsShift(string op) => op is "<<" or ">>";
        static bool IsComparison(string op) => op is "==" or "!=" or "<" or ">" or "<=" or ">=";

        return (IsLogical(parentOp) && IsLogical(childOp))
            || IsBitwise(parentOp)
            || IsShift(parentOp)
            || IsShift(childOp)
            || (IsComparison(parentOp) && IsComparison(childOp));
    }

    /// <summary>True when an expression is a literal numeric zero (<c>0</c>, <c>0.</c>, <c>0.0</c>), used to
    /// recognize a base-level <c>textureLod(s, uv, 0)</c> that can lower to a plain <c>tex2D</c>.</summary>
    private static bool IsLiteralZero(Expr e) => e switch
    {
        // An unsigned suffix ('0u') is part of the token text; trim it before the numeric parse.
        IntLiteralExpr i => long.TryParse(i.Text.TrimEnd('u', 'U'), out long n) && n == 0,
        FloatLiteralExpr f => double.TryParse(
            f.Text.TrimEnd('f', 'F'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double d) && d == 0.0,
        _ => false,
    };

    private Emitted EmitCall(CallExpr call)
    {
        string name = call.Callee;
        List<Emitted> emittedArgs = call.Args.Select(Emit).ToList();
        List<string> args = emittedArgs.Select(e => e.Text).ToList();

        // Struct constructor (G6): GLSL `Name(a, b)` -> the generated factory `make_Name(a, b)`. The
        // arg count must match the struct's member count (a wrong arity is a loud reject).
        if (_structs.TryGetValue(name, out StructDecl? sd))
        {
            if (call.Args.Count != sd.Members.Count)
            {
                throw Reject(call,
                    $"Struct constructor '{name}' expects {sd.Members.Count} argument(s) " +
                    $"(one per member), got {call.Args.Count}.");
            }

            return Postfix($"make_{name}({string.Join(", ", args)})");
        }

        // Type constructor.
        if (TypeTable.IsTypeName(name))
        {
            return EmitConstructor(name, emittedArgs, call);
        }

        // Special-cased intrinsics.
        switch (name)
        {
            case "atan":
                if (call.Args.Count == 2)
                {
                    return Postfix($"atan2({args[0]}, {args[1]})");
                }

                if (call.Args.Count == 1)
                {
                    return Postfix($"atan({args[0]})");
                }

                throw Reject(call, $"'atan' takes 1 or 2 arguments, got {call.Args.Count}.");

            case "mod":
                if (call.Args.Count != 2)
                {
                    throw Reject(call, $"'mod' takes 2 arguments, got {call.Args.Count}.");
                }

                UsedGlslMod = true;
                return Postfix($"glsl_mod({args[0]}, {args[1]})");

            case "matrixCompMult":
                // GLSL matrixCompMult(a, b) is the COMPONENTWISE matrix product. HLSL `*` on matrices
                // is already componentwise (only `mul` is the linear-algebra product), so emit `a * b`
                // directly — NOT through the matrix-order trap in EmitBinary.
                if (call.Args.Count != 2)
                {
                    throw Reject(call, $"'matrixCompMult' takes 2 arguments, got {call.Args.Count}.");
                }

                return new Emitted(
                    $"{WrapOperand("*", emittedArgs[0], isRight: false)} * {WrapOperand("*", emittedArgs[1], isRight: true)}",
                    PrecMultiplicative,
                    "*");

            case "texture" or "texture2D":
                RegisterChannelArg(call);
                if (call.Args.Count == 3)
                {
                    // texture(sampler, uv, bias) would map to tex2Dbias. That legacy intrinsic compiled
                    // only on FNA's fx_2_0 path until 2026-10-28; the FX pre-parser now rewrites it on
                    // every target, but this route has never been render-validated with it (the ShaderToy
                    // gates bind no mip chain), so reject loudly at convert time and keep the boundary
                    // explicit and located.
                    throw Reject(call,
                        $"The mip-bias texture form '{name}(sampler, uv, bias)' is outside the supported " +
                        "subset of the ShaderToy route (it is not yet render-validated there).");
                }

                if (call.Args.Count != 2)
                {
                    throw Reject(call, $"'{name}' expects (sampler, uv).");
                }

                // A 3D (vec3) coordinate means iChannelN is being sampled as a CUBEMAP (a direction lookup),
                // e.g. texture(iChannel0, reflect(rd, n)). The single-pass 2D harness binds each iChannelN as
                // a 2D sampler, so there is no faithful mapping. Reject it clearly here: otherwise the vec3
                // coordinate silently truncates to 2D and the user sees an opaque "-Wconversion" truncation
                // error on generated HLSL instead of the real reason.
                GlslType texCoordType = _types.Infer(call.Args[1]);
                if (texCoordType.IsVector && texCoordType.Rows >= 3)
                {
                    throw Reject(call,
                        $"'{name}(sampler, vec3)' samples a CUBEMAP (a 3D direction lookup), which is outside " +
                        "the supported subset: the single-pass 2D harness binds each iChannelN as a 2D " +
                        "sampler, so a cubemap channel has no faithful 2D mapping.");
                }

                return Postfix($"tex2D({string.Join(", ", args)})");

            case "textureLod":
                // tex2Dlod takes a float4 (uv.xy, 0, lod). ShaderToy textureLod(s, uv, lod).
                RegisterChannelArg(call);
                if (call.Args.Count != 3)
                {
                    throw Reject(call, "'textureLod' expects (sampler, uv, lod).");
                }

                // textureLod(s, uv, 0) is base-level sampling: emit a plain tex2D. The single-pass
                // harness binds each iChannelN without mipmaps, so mip 0 is the only level and tex2D is
                // equivalent. This keeps the common `textureLod(iChannel0, uv, 0.)` form working on
                // every backend.
                if (IsLiteralZero(call.Args[2]))
                {
                    return Postfix($"tex2D({args[0]}, {args[1]})");
                }

                // A NON-zero LOD would emit the legacy tex2Dlod intrinsic. That intrinsic compiled only
                // on FNA's fx_2_0 path until 2026-10-28 (FX0012 elsewhere); the FX pre-parser now
                // rewrites it on every target, but this route has never been render-validated with it
                // (the ShaderToy gates bind no mip chain), so the subset stays closed and the boundary
                // stays an explicit, located convert-time reject, like the mip-bias form above.
                throw Reject(call,
                    "The explicit-LOD texture form 'textureLod(sampler, uv, lod)' with a non-zero " +
                    "lod is outside the supported subset of the ShaderToy route (it is not yet " +
                    "render-validated there). A base-level 'textureLod(sampler, uv, 0.)' is " +
                    "supported and lowers to a plain tex2D.");

            case "textureGrad":
                RegisterChannelArg(call);
                if (call.Args.Count != 4)
                {
                    throw Reject(call, "'textureGrad' expects (sampler, uv, ddx, ddy).");
                }

                return Postfix($"tex2Dgrad({args[0]}, {args[1]}, {args[2]}, {args[3]})");
        }

        // Simple rename table.
        if (IntrinsicTable.Renames.TryGetValue(name, out string? hlsl))
        {
            return Postfix($"{hlsl}({string.Join(", ", args)})");
        }

        // Same-name HLSL intrinsic.
        if (IntrinsicTable.SameName.Contains(name))
        {
            return Postfix($"{name}({string.Join(", ", args)})");
        }

        // Explicitly rejected intrinsic.
        if (IntrinsicTable.Rejected.TryGetValue(name, out string? reason))
        {
            throw Reject(call, reason);
        }

        // User-defined function (resolved/validated by the Converter). Emit verbatim, applying any F1
        // reserved-word rename to the callee (a local rename never affects a call head — the call binds
        // to the function it names, which is why a local that shadows a called function is renamed instead).
        if (_userFunctions.Contains(name))
        {
            return Postfix($"{MapGlobal(name)}({string.Join(", ", args)})");
        }

        throw Reject(call,
            $"Unknown function or intrinsic '{name}' is not a user function and not in the mapping table.");
    }

    /// <summary>
    /// Known GLSL stage built-ins that the single-pass 2D fullscreen harness cannot provide a value for,
    /// mapped to a precise (named) reject message — better than a generic "undeclared identifier."
    /// (<c>gl_FragCoord</c> IS supported and is handled before this point.)
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownUnsupportedGlBuiltins =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gl_FragDepth"] =
                "'gl_FragDepth' (per-fragment depth output) is outside the supported subset: a 2D " +
                "fullscreen image pass has no meaningful depth output.",
            ["gl_FrontFacing"] =
                "'gl_FrontFacing' (front/back face flag) is outside the supported subset: the fullscreen " +
                "harness draws a single front-facing quad.",
            ["gl_TexCoord"] =
                "'gl_TexCoord' (legacy fixed-function texture coordinate) is outside the supported " +
                "subset; use 'fragCoord'/'gl_FragCoord' or a declared uniform instead.",
            ["gl_FragData"] =
                "'gl_FragData' (multiple render targets) is outside the supported subset (single image " +
                "output only).",
        };

    private readonly HashSet<string> _userFunctions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _customUniforms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _screenUvAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StructDecl> _structs = new(StringComparer.Ordinal);

    // F1 identifier-safety renames (IdentifierSafety). _globalRenames maps a top-level user identifier
    // (function / const-or-mutable global) whose name is an HLSL reserved keyword to a safe name, applied
    // to its declaration AND every reference (value + call). _localRenames is the CURRENT function's
    // local/param renames (set before each EmitFunction), applied to local declarations and VALUE
    // references only — a call head stays bound to the function it names. Both are empty for a shader
    // with no collisions, so they change nothing for a clean shader.
    private IReadOnlyDictionary<string, string> _globalRenames =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _localRenames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Legacy HLSL for-scope renames (IdentifierSafety.ForLoopLocals): per-loop init-variable renames keyed
    // by the loop node, applied while emitting that loop ONLY. _loopRenameScopes is the stack of currently
    // active loop renames (innermost last, pushed/popped by EmitFor); it is consulted BEFORE the
    // function-wide _localRenames so a renamed loop variable shadows every other meaning of the name within
    // its loop. Empty for a shader that never reuses a for-loop variable.
    private IReadOnlyDictionary<ForStmt, Dictionary<string, string>> _forLoopRenames =
        new Dictionary<ForStmt, Dictionary<string, string>>(ReferenceEqualityComparer.Instance);
    private readonly List<Dictionary<string, string>> _loopRenameScopes = new();

    /// <summary>Register the top-level reserved-word renames (applied to declarations + references).</summary>
    public void SetGlobalRenames(IReadOnlyDictionary<string, string> renames) => _globalRenames = renames;

    /// <summary>Set the current function's local/param renames (call before <see cref="EmitFunction"/>).
    /// Pass an empty map for a function with no renamed locals.</summary>
    public void SetLocalRenames(IReadOnlyDictionary<string, string> renames) => _localRenames = renames;

    /// <summary>Register the per-for-loop init-variable renames (keyed by loop node, applied to that loop's
    /// init + body only). Set once for the whole shader; empty for a shader with no for-loop reuse.</summary>
    public void SetForLoopRenames(IReadOnlyDictionary<ForStmt, Dictionary<string, string>> renames) =>
        _forLoopRenames = renames;

    private string MapGlobal(string name) =>
        _globalRenames.TryGetValue(name, out string? r) ? r : name;

    private string MapLocal(string name)
    {
        TryRenameLocal(name, out string mapped);
        return mapped;
    }

    /// <summary>Resolve a local/parameter name to its emitted form: an active for-loop variable rename
    /// (innermost scope first) takes precedence over the function-wide F1 local rename. Returns false (and
    /// the name unchanged) when nothing renames it.</summary>
    private bool TryRenameLocal(string name, out string mapped)
    {
        for (int s = _loopRenameScopes.Count - 1; s >= 0; s--)
        {
            if (_loopRenameScopes[s].TryGetValue(name, out string? lr))
            {
                mapped = lr;
                return true;
            }
        }

        if (_localRenames.TryGetValue(name, out string? r))
        {
            mapped = r;
            return true;
        }

        mapped = name;
        return false;
    }

    /// <summary>Register the user-defined structs (G6) so struct-typed declarations spell their HLSL
    /// type as the struct name and struct constructors route to the generated factory.</summary>
    public void SetStructs(IEnumerable<StructDecl> structs)
    {
        _structs.Clear();
        foreach (StructDecl s in structs)
        {
            _structs[s.Name] = s;
        }
    }

    /// <summary>The HLSL type spelling for a GLSL type spelling, passing a user struct name through
    /// unchanged (HLSL struct syntax matches) and mapping built-ins via the type table.</summary>
    private string HlslType(string glslType) =>
        _structs.ContainsKey(glslType) ? glslType : TypeTable.ToHlsl(glslType);

    /// <summary>
    /// Emit a user-defined <c>struct</c> declaration (G6) plus a factory function the converter uses in
    /// place of a GLSL struct constructor. GLSL's <c>Name(a, b)</c> constructor has no HLSL equivalent,
    /// so each <c>Name(...)</c> call is rewritten to <c>make_Name(...)</c>, and this emits:
    /// <code>
    /// struct Name { float3 a; float b; };
    /// Name make_Name(float3 a, float b) { Name s; s.a = a; s.b = b; return s; }
    /// </code>
    /// </summary>
    public string EmitStruct(StructDecl s)
    {
        _sb.Clear();
        _indent = 0;

        Line($"struct {s.Name}");
        Line("{");
        _indent++;
        foreach (StructMember m in s.Members)
        {
            // An array member spells its size on the member name (HLSL, matching GLSL): `float w[4];`.
            Line(m.ArraySize is { } n
                ? $"{HlslType(m.TypeName)} {m.Name}[{n}];"
                : $"{HlslType(m.TypeName)} {m.Name};");
        }

        _indent--;
        Line("};");

        // Factory: make_Name(<members>) building and returning the struct, field by field. An array
        // member becomes an array parameter (size on the name) and is copied element-by-element, since
        // HLSL has no whole-array assignment in the FX9 / SM3 target. (A struct with array members rarely
        // uses its positional constructor; we still provide a faithful factory so `Name(...)` resolves.)
        string paramList = string.Join(", ", s.Members.Select(m =>
            m.ArraySize is { } n
                ? $"{HlslType(m.TypeName)} {m.Name}[{n}]"
                : $"{HlslType(m.TypeName)} {m.Name}"));
        Line($"{s.Name} make_{s.Name}({paramList})");
        Line("{");
        _indent++;
        Line($"{s.Name} result;");
        foreach (StructMember m in s.Members)
        {
            if (m.ArraySize is { } n)
            {
                // HLSL (FX9/SM3) has no whole-array assignment; copy element by element.
                for (int e = 0; e < n; e++)
                {
                    Line($"result.{m.Name}[{e}] = {m.Name}[{e}];");
                }
            }
            else
            {
                Line($"result.{m.Name} = {m.Name};");
            }
        }

        Line("return result;");
        _indent--;
        Line("}");
        return _sb.ToString();
    }

    /// <summary>Register the set of user-defined function names so calls to them are accepted.</summary>
    public void SetUserFunctions(IEnumerable<string> names)
    {
        _userFunctions.Clear();
        foreach (string n in names)
        {
            _userFunctions.Add(n);
        }
    }

    /// <summary>Register the declared custom-uniform names so references to them are accepted and
    /// emitted verbatim (the harness declares each as an effect parameter the consumer drives).</summary>
    public void SetCustomUniforms(IEnumerable<string> names)
    {
        _customUniforms.Clear();
        foreach (string n in names)
        {
            _customUniforms.Add(n);
        }
    }

    /// <summary>Register the screen-coordinate alias names (ignored coordinate varyings +
    /// <c>openfl_TextureCoordv</c>) so a reference to one resolves to the harness <c>sd_ScreenUV</c>.</summary>
    public void SetScreenUvAliases(IEnumerable<string> names)
    {
        _screenUvAliases.Clear();
        foreach (string n in names)
        {
            _screenUvAliases.Add(n);
        }
    }

    private void RegisterChannelArg(CallExpr call)
    {
        if (call.Args.Count > 0 && call.Args[0] is IdentifierExpr id && UniformInfo.IsChannel(id.Name))
        {
            ReferencedUniforms.Add(id.Name);
        }
    }

    private Emitted EmitConstructor(string glslType, List<Emitted> emittedArgs, CallExpr call)
    {
        List<string> args = emittedArgs.Select(e => e.Text).ToList();
        string hlsl = TypeTable.ToHlsl(glslType);
        GlslType t = TypeTable.Resolve(glslType);

        // GLSL splat: vecN(scalar) fills all N components with the scalar. HLSL has no
        // single-scalar vector constructor, so expand it to a cast, which splats cleanly.
        // (Only when the single argument is itself a scalar; vecN(vecM) component-promotion below.)
        if (t.IsVector && call.Args.Count == 1)
        {
            GlslType argType = _types.Infer(call.Args[0]);
            if (argType.IsScalar || !argType.IsKnown)
            {
                // (floatN)scalar splats the scalar to every component in HLSL.
                return Cast(hlsl, emittedArgs[0]);
            }

            // vecN(vecM) where M >= N (e.g. vec3(someVec4)): HLSL needs an explicit swizzle/cast.
            if (argType.IsVector)
            {
                return Cast(hlsl, emittedArgs[0]);
            }
        }

        // Matrix constructor: pass the identical scalar list to the HLSL floatNxN constructor.
        // This intentionally yields the transpose of the GLSL matrix; the reversed mul() order in
        // EmitBinary cancels it so M*v matches GLSL. (See class remarks for the full proof.)
        // A SINGLE-argument matrix constructor has well-defined GLSL semantics that HLSL lacks a builtin
        // for, so we expand it explicitly (the diagonal of the result is symmetric, so the transpose
        // question is moot — a diagonal matrix and a submatrix-of-a-symmetric-or-not are emitted as the
        // exact scalar grid GLSL specifies):
        //   - matN(scalar s): the GLSL diagonal matrix (s on the diagonal, 0 elsewhere). e.g. mat3(1) is
        //     the identity. We expand to floatNxN(s,0,0, 0,s,0, 0,0,s).
        //   - matN(matM m): GLSL takes the upper-left min(N,M) submatrix of m and fills any remaining
        //     diagonal with 1 and off-diagonal with 0 (the identity completion). We expand to the
        //     floatNxN grid reading m[r][c] where both indices are < M, else the identity value.
        if (t.IsMatrix && args.Count == 1)
        {
            GlslType argType = _types.Infer(call.Args[0]);
            int n = t.Rows;
            if (argType.IsMatrix)
            {
                return Postfix(EmitMatrixFromMatrix(hlsl, n, argType.Rows, Wrap(emittedArgs[0], PrecPostfix)));
            }

            if (argType.IsScalar || !argType.IsKnown)
            {
                return Postfix(EmitDiagonalMatrix(hlsl, n, args[0]));
            }

            // A single VECTOR whose component count fills the matrix (GLSL flattens components
            // column-major): e.g. mat2(vec4) takes the 4 components. HLSL's floatNxN(...) constructor
            // flattens a vector argument in the same component order, which (like the scalar-list path)
            // yields the transpose of the GLSL matrix; the reversed mul() order cancels it (trap 2). So
            // pass the vector straight through. A vector of the wrong width is a loud reject.
            if (argType.IsVector && argType.Rows == n * n)
            {
                return Postfix($"{hlsl}({args[0]})");
            }

            // A single vector argument of the wrong width is not a defined GLSL matrix constructor.
            throw Reject(call,
                $"Single-argument matrix constructor '{glslType}(x)' with a non-scalar, non-matrix " +
                $"argument that does not supply exactly {n * n} components is outside the supported subset.");
        }

        return Postfix($"{hlsl}({string.Join(", ", args)})");
    }

    /// <summary>
    /// Expand a GLSL single-scalar matrix constructor <c>matN(s)</c> (the diagonal matrix: <c>s</c> on
    /// the diagonal, 0 elsewhere; <c>mat3(1)</c> is the identity) to an explicit HLSL
    /// <c>floatNxN(...)</c> grid. The diagonal matrix is symmetric, so emitting the grid directly is
    /// consistent with the trap-2 transpose convention (Mᵀ == M for a diagonal matrix). The scalar is
    /// evaluated once into a temp-free inline expression; to avoid re-evaluating a non-trivial scalar
    /// expression N times it is wrapped so only the literal/identifier common case stays terse.
    /// </summary>
    private static string EmitDiagonalMatrix(string hlsl, int n, string scalar)
    {
        var cells = new List<string>(n * n);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                cells.Add(r == c ? scalar : "0.0");
            }
        }

        return $"{hlsl}({string.Join(", ", cells)})";
    }

    /// <summary>
    /// Expand a GLSL matrix-from-matrix constructor <c>matN(matM m)</c> to an explicit HLSL
    /// <c>floatNxN(...)</c> grid following GLSL semantics: take the upper-left <c>min(N,M)</c> submatrix of
    /// <c>m</c> and complete any remaining diagonal with 1 / off-diagonal with 0 (the identity
    /// completion). Because the stored HLSL matrix is the transpose of the GLSL matrix (trap 2), and the
    /// result must keep the same convention, the emitted HLSL cell <c>[r][c]</c> reads the SAME HLSL cell
    /// <c>m[r][c]</c> for r,c &lt; M (the two transposes cancel), so a direct component copy is correct.
    /// </summary>
    private static string EmitMatrixFromMatrix(string hlsl, int n, int m, string mref)
    {
        // mref arrives already wrapped (by the caller) when it is looser than postfix, so the indexing
        // binds to the whole value.
        var cells = new List<string>(n * n);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (r < m && c < m)
                {
                    cells.Add($"{mref}[{r}][{c}]");
                }
                else
                {
                    cells.Add(r == c ? "1.0" : "0.0");
                }
            }
        }

        return $"{hlsl}({string.Join(", ", cells)})";
    }

    /// <summary>
    /// Emit a GLSL array constructor (G7) as an HLSL brace initializer list <c>{ a, b, c }</c>. HLSL
    /// has no array-constructor call syntax; a brace list is valid at a declaration initializer site,
    /// which is the only place the supported subset allows an array constructor.
    /// </summary>
    private string EmitArrayConstructor(ArrayConstructorExpr ac) =>
        $"{{ {string.Join(", ", ac.Elements.Select(EmitExpr))} }}";

    private static ConvertException Reject(Expr at, string message) =>
        new(message, at.Line, at.Column);

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>Translate a GLSL swizzle/member to HLSL (rgba/stpq → xyzw; xyzw passthrough).</summary>
    private static string TranslateSwizzle(string member)
    {
        // HLSL accepts .xyzw and .rgba but not .stpq; normalize stpq → xyzw and keep rgba as-is.
        if (member.IndexOfAny(new[] { 's', 't', 'p', 'q' }) < 0)
        {
            return member;
        }

        // Only translate when ALL chars are from the stpq set (a texture-coord swizzle); otherwise
        // it is already a valid xyzw/rgba selector (e.g. ".x" contains none of stpq anyway).
        var sb = new StringBuilder(member.Length);
        foreach (char c in member)
        {
            sb.Append(c switch
            {
                's' => 'x',
                't' => 'y',
                'p' => 'z',
                'q' => 'w',
                _ => c,
            });
        }

        return sb.ToString();
    }

    /// <summary>Ensure a float literal carries a decimal point so HLSL types it as float, not int.</summary>
    private static string NormalizeFloat(string text)
    {
        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
        {
            return text;
        }

        return text + ".0";
    }
}
