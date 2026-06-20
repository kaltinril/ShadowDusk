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

        string ret = TypeTable.ToHlsl(fn.ReturnType);
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
            ps.Add($"{qual}{TypeTable.ToHlsl(p.TypeName)} {p.Name}");
            _types.Declare(p.Name, p.TypeName);
        }

        Line($"{ret} {fn.Name}({string.Join(", ", ps)})");
        EmitBlock(fn.Body);
        _types.PopScope();
        return _sb.ToString();
    }

    /// <summary>Emit a top-level <c>const</c> global.</summary>
    public string EmitGlobalConst(GlobalConstDecl g)
    {
        _sb.Clear();
        _indent = 0;
        string type = TypeTable.ToHlsl(g.TypeName);
        Line($"static const {type} {g.Name} = {EmitExpr(g.Initializer)};");
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
        string type = TypeTable.ToHlsl(g.TypeName);
        if (g.Initializer is null)
        {
            Line($"static {type} {g.Name};");
        }
        else
        {
            Line($"static {type} {g.Name} = {EmitInitializer(g.TypeName, g.Initializer)};");
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
        foreach (Stmt s in block.Statements)
        {
            EmitStatement(s);
        }

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
                Line($"{EmitExpr(e.Expression)};");
                break;
            case IfStmt i:
                EmitIf(i);
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
        _types.Declare(v.Name, v.TypeName);
        string type = TypeTable.ToHlsl(v.TypeName);
        string prefix = v.IsConst ? "const " : string.Empty;
        if (v.Initializer is null)
        {
            Line($"{prefix}{type} {v.Name};");
        }
        else
        {
            Line($"{prefix}{type} {v.Name} = {EmitInitializer(v.TypeName, v.Initializer)};");
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
        string emitted = EmitExpr(value);
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

        // Parenthesize so the swizzle binds to the whole value, then select the leading components.
        return $"({emitted}).{swizzle}";
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

    private void EmitFor(ForStmt f)
    {
        // Render init / cond / inc inline. A var-decl init is rendered without trailing newline.
        string init = f.Init switch
        {
            null => string.Empty,
            VarDeclStmt vd => RenderInlineVarDecl(vd),
            MultiDeclStmt md => RenderInlineMultiDecl(md),
            ExprStmt es => EmitExpr(es.Expression),
            _ => string.Empty,
        };
        string cond = f.Condition is null ? string.Empty : EmitExpr(f.Condition);
        string inc = f.Increment is null ? string.Empty : EmitExpr(f.Increment);
        Line($"for ({init}; {cond}; {inc})");
        EmitBody(f.Body);
    }

    private string RenderInlineVarDecl(VarDeclStmt v)
    {
        _types.Declare(v.Name, v.TypeName);
        string type = TypeTable.ToHlsl(v.TypeName);
        return v.Initializer is null
            ? $"{type} {v.Name}"
            : $"{type} {v.Name} = {EmitInitializer(v.TypeName, v.Initializer)}";
    }

    private string RenderInlineMultiDecl(MultiDeclStmt m)
    {
        // GLSL `for (int i = 0, n = 4; ...)` -> HLSL `int i = 0, n = 4`.
        string type = TypeTable.ToHlsl(m.Declarators[0].TypeName);
        var parts = new List<string>();
        foreach (VarDeclStmt d in m.Declarators)
        {
            _types.Declare(d.Name, d.TypeName);
            parts.Add(d.Initializer is null ? d.Name : $"{d.Name} = {EmitInitializer(d.TypeName, d.Initializer)}");
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
            EmitStatement(body);
            _indent--;
            Line("}");
        }
    }

    // ── expressions ────────────────────────────────────────────────────────────

    /// <summary>Translate an expression to an HLSL string.</summary>
    public string EmitExpr(Expr expr) => expr switch
    {
        IntLiteralExpr i => i.Text,
        FloatLiteralExpr f => NormalizeFloat(f.Text),
        BoolLiteralExpr b => b.Value ? "true" : "false",
        IdentifierExpr id => EmitIdentifier(id),
        SwizzleExpr sw => $"{EmitExpr(sw.Target)}.{TranslateSwizzle(sw.Member)}",
        IndexExpr idx => EmitIndex(idx),
        CallExpr call => EmitCall(call),
        UnaryExpr un => EmitUnary(un),
        BinaryExpr bin => EmitBinary(bin),
        ConditionalExpr c => $"({EmitCondition(c.Condition)} ? {EmitExpr(c.WhenTrue)} : {EmitExpr(c.WhenFalse)})",
        AssignExpr a => EmitAssign(a),
        _ => throw new ConvertException("Internal: unhandled expression.", expr.Line, expr.Column),
    };

    /// <summary>
    /// Emit an assignment, with one trap (B1): a compound <c>*=</c> whose right-hand side is a matrix
    /// must honor the same matrix-multiply reordering as a binary <c>*</c>. GLSL <c>v *= M</c> means
    /// <c>v = M*v</c>; under the converter's <c>A*B → mul(B,A)</c> rule that is <c>v = mul(v, M)</c>.
    /// A plain <c>v *= M</c> would emit invalid HLSL (<c>float2 *= float2x2</c>). Scalar/vector
    /// <c>*=</c> (and every other compound op) stays component-wise and passes through unchanged.
    /// </summary>
    private string EmitAssign(AssignExpr a)
    {
        if (a.Op == "*=")
        {
            GlslType targetType = _types.Infer(a.Target);
            GlslType valueType = _types.Infer(a.Value);
            if (targetType.IsMatrix || valueType.IsMatrix)
            {
                // Desugar `lhs *= rhs` to `lhs = (rhs * lhs)` and route the multiply through the
                // binary path so the matrix-order trap applies consistently. For `v *= M` this
                // yields `v = mul(v, M)`; the `(rhs * lhs)` order is what makes EmitBinary place the
                // matrix as the second mul() argument.
                var product = new BinaryExpr
                {
                    Op = "*",
                    Left = a.Value,
                    Right = a.Target,
                    Line = a.Line,
                    Column = a.Column,
                };
                return $"{EmitExpr(a.Target)} = {EmitBinary(product)}";
            }
        }

        // B4: a plain assignment whose RHS is a wider vector than the LHS truncates implicitly in
        // GLSL but errors under stricter HLSL; make the truncation explicit with a swizzle.
        if (a.Op == "=")
        {
            GlslType targetType = _types.Infer(a.Target);
            return $"{EmitExpr(a.Target)} = {TruncateToWidth(targetType, a.Value)}";
        }

        return $"{EmitExpr(a.Target)} {a.Op} {EmitExpr(a.Value)}";
    }

    /// <summary>
    /// Emit an expression used in a BOOLEAN context (an <c>if</c>/<c>while</c>/<c>do…while</c>/ternary
    /// condition). Two traps are handled here:
    /// <list type="bullet">
    /// <item><b>B2 — over-parenthesization.</b> The call site already wraps the condition in
    /// <c>(...)</c>, so a top-level binary must NOT add its own outer parens; otherwise
    /// <c>if (a == 0.0)</c> becomes <c>if ((a == 0.0))</c>, which fxc rejects under
    /// <c>-Werror,-Wparentheses-equality</c>.</item>
    /// <item><b>B3 — vector equality.</b> GLSL <c>vecA == vecB</c> in a bool context is a single
    /// bool; HLSL <c>==</c> on vectors yields a bool-vector, so it must be reduced with
    /// <c>all(a == b)</c> (and <c>!=</c> with <c>any(a != b)</c>). The reduction recurses through
    /// <c>&amp;&amp;</c>/<c>||</c>/<c>!</c> so a nested vector comparison is also scalarized.</item>
    /// </list>
    /// </summary>
    private string EmitCondition(Expr expr)
    {
        switch (expr)
        {
            case BinaryExpr bin when bin.Op is "==" or "!=":
            {
                GlslType lt = _types.Infer(bin.Left);
                GlslType rt = _types.Infer(bin.Right);
                if (lt.IsVector || rt.IsVector)
                {
                    string reducer = bin.Op == "==" ? "all" : "any";
                    return $"{reducer}({EmitExpr(bin.Left)} {bin.Op} {EmitExpr(bin.Right)})";
                }

                // Scalar equality: emit without the redundant outer parens (B2).
                return $"{EmitExpr(bin.Left)} {bin.Op} {EmitExpr(bin.Right)}";
            }

            case BinaryExpr bin when bin.Op is "&&" or "||":
                // Recurse so a vector comparison on either side is still scalarized; keep parens
                // around each side to preserve precedence.
                return $"({EmitCondition(bin.Left)}) {bin.Op} ({EmitCondition(bin.Right)})";

            case UnaryExpr un when un.Op == "!" && !un.IsPostfix:
                return $"!({EmitCondition(un.Operand)})";

            case BinaryExpr bin when bin.Op is "<" or ">" or "<=" or ">=":
                // Other top-level relational comparisons: drop the redundant outer parens the
                // generic EmitBinary would add (B2 generalizes to all top-level comparisons).
                return $"{EmitExpr(bin.Left)} {bin.Op} {EmitExpr(bin.Right)}";

            default:
                // Arithmetic / call / identifier condition: keep the generic emission (which retains
                // matrix handling and any needed parens).
                return EmitExpr(expr);
        }
    }

    private string EmitIdentifier(IdentifierExpr id)
    {
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

        if (!_types.IsKnownIdentifier(id.Name) && !_userFunctions.Contains(id.Name))
        {
            // A free (undeclared) identifier: not a local/param/const-global, not a ShaderToy
            // uniform, not a user function. Reject loudly at convert time rather than leaking it to
            // HLSL where it surfaces as "use of undeclared identifier". (Custom uniforms, ISF
            // builtins like RENDERSIZE, etc. land here.)
            throw new ConvertException(
                $"Undeclared identifier '{id.Name}'. It is not a local variable, a 'const' global, " +
                "a user function, a declared custom uniform, or a predefined ShaderToy uniform (iTime, " +
                "iResolution, iMouse, iChannelN, ...). Declare it as a top-level 'uniform' to expose it " +
                "as an effect parameter.",
                id.Line, id.Column, id.Name);
        }

        return id.Name;
    }

    private string EmitIndex(IndexExpr idx)
    {
        // Track the array uniforms (iChannelTime / iChannelResolution) when indexed.
        if (idx.Target is IdentifierExpr id && UniformInfo.IsUniform(id.Name))
        {
            ReferencedUniforms.Add(id.Name);
        }

        return $"{EmitExpr(idx.Target)}[{EmitExpr(idx.Index)}]";
    }

    private string EmitUnary(UnaryExpr un)
    {
        string operand = EmitExpr(un.Operand);
        return un.IsPostfix ? $"{operand}{un.Op}" : $"{un.Op}{operand}";
    }

    private string EmitBinary(BinaryExpr bin)
    {
        string l = EmitExpr(bin.Left);
        string r = EmitExpr(bin.Right);

        if (bin.Op == "*")
        {
            GlslType lt = _types.Infer(bin.Left);
            GlslType rt = _types.Infer(bin.Right);

            // Matrix multiply (trap 2). GLSL A * B  →  HLSL mul(B, A) preserves column-major semantics
            // given that matrix constructors are emitted with the identical (now transposed) scalar list.
            // A scalar operand is NOT a matrix multiply — it is component scaling and stays as `*`.
            bool leftMat = lt.IsMatrix;
            bool rightMat = rt.IsMatrix;
            if (leftMat || rightMat)
            {
                bool leftScalar = lt.IsScalar;
                bool rightScalar = rt.IsScalar;
                if (leftScalar || rightScalar)
                {
                    // scalar * matrix or matrix * scalar → plain componentwise scale.
                    return $"({l} * {r})";
                }

                return $"mul({r}, {l})";
            }
        }

        return $"({l} {bin.Op} {r})";
    }

    private string EmitCall(CallExpr call)
    {
        string name = call.Callee;
        List<string> args = call.Args.Select(EmitExpr).ToList();

        // Type constructor.
        if (TypeTable.IsTypeName(name))
        {
            return EmitConstructor(name, args, call);
        }

        // Special-cased intrinsics.
        switch (name)
        {
            case "atan":
                if (call.Args.Count == 2)
                {
                    return $"atan2({args[0]}, {args[1]})";
                }

                if (call.Args.Count == 1)
                {
                    return $"atan({args[0]})";
                }

                throw Reject(call, $"'atan' takes 1 or 2 arguments, got {call.Args.Count}.");

            case "mod":
                if (call.Args.Count != 2)
                {
                    throw Reject(call, $"'mod' takes 2 arguments, got {call.Args.Count}.");
                }

                UsedGlslMod = true;
                return $"glsl_mod({args[0]}, {args[1]})";

            case "texture" or "texture2D":
                RegisterChannelArg(call);
                return $"tex2D({string.Join(", ", args)})";

            case "textureLod":
                // tex2Dlod takes a float4 (uv.xy, 0, lod). ShaderToy textureLod(s, uv, lod).
                RegisterChannelArg(call);
                if (call.Args.Count != 3)
                {
                    throw Reject(call, "'textureLod' expects (sampler, uv, lod).");
                }

                return $"tex2Dlod({args[0]}, float4(({args[1]}), 0, ({args[2]})))";

            case "textureGrad":
                RegisterChannelArg(call);
                if (call.Args.Count != 4)
                {
                    throw Reject(call, "'textureGrad' expects (sampler, uv, ddx, ddy).");
                }

                return $"tex2Dgrad({args[0]}, {args[1]}, {args[2]}, {args[3]})";
        }

        // Simple rename table.
        if (IntrinsicTable.Renames.TryGetValue(name, out string? hlsl))
        {
            return $"{hlsl}({string.Join(", ", args)})";
        }

        // Same-name HLSL intrinsic.
        if (IntrinsicTable.SameName.Contains(name))
        {
            return $"{name}({string.Join(", ", args)})";
        }

        // Explicitly rejected intrinsic.
        if (IntrinsicTable.Rejected.TryGetValue(name, out string? reason))
        {
            throw Reject(call, reason);
        }

        // User-defined function (resolved/validated by the Converter). Emit verbatim.
        if (_userFunctions.Contains(name))
        {
            return $"{name}({string.Join(", ", args)})";
        }

        throw Reject(call,
            $"Unknown function or intrinsic '{name}' is not a user function and not in the mapping table.");
    }

    private readonly HashSet<string> _userFunctions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _customUniforms = new(StringComparer.Ordinal);

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

    private void RegisterChannelArg(CallExpr call)
    {
        if (call.Args.Count > 0 && call.Args[0] is IdentifierExpr id && UniformInfo.IsChannel(id.Name))
        {
            ReferencedUniforms.Add(id.Name);
        }
    }

    private string EmitConstructor(string glslType, List<string> args, CallExpr call)
    {
        string hlsl = TypeTable.ToHlsl(glslType);
        GlslType t = TypeTable.Resolve(glslType);

        // GLSL splat: vecN(scalar) fills all N components with the scalar. HLSL has no
        // single-scalar vector constructor, so expand it to (x)0-style cast which splats cleanly.
        // (Only when the single argument is itself a scalar; vecN(vecM) component-promotion below.)
        if (t.IsVector && call.Args.Count == 1)
        {
            GlslType argType = _types.Infer(call.Args[0]);
            if (argType.IsScalar || !argType.IsKnown)
            {
                // ((floatN)(scalar)) splats the scalar to every component in HLSL.
                return $"(({hlsl})({args[0]}))";
            }

            // vecN(vecM) where M >= N (e.g. vec3(someVec4)): HLSL needs an explicit swizzle/cast.
            if (argType.IsVector)
            {
                return $"(({hlsl})({args[0]}))";
            }
        }

        // Matrix constructor: pass the identical scalar list to the HLSL floatNxN constructor.
        // This intentionally yields the transpose of the GLSL matrix; the reversed mul() order in
        // EmitBinary cancels it so M*v matches GLSL. (See class remarks for the full proof.)
        // HLSL has no `floatNxN(scalar)` single-arg diagonal constructor, so a 1-arg matrix
        // constructor is rejected (rare in ShaderToy image shaders; reject loudly, never guess).
        if (t.IsMatrix && args.Count == 1)
        {
            throw Reject(call,
                $"Single-argument matrix constructor '{glslType}(x)' is outside the supported subset " +
                "(HLSL has no diagonal floatNxN(scalar) form). Use an explicit component list.");
        }

        return $"{hlsl}({string.Join(", ", args)})";
    }

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
