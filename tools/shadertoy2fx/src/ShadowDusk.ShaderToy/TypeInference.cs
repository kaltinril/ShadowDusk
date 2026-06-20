using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// A lightweight type-inference pass: enough to classify any expression as scalar / vector /
/// matrix / sampler. The emitter relies on this for the two semantic traps: choosing
/// <c>mul(...)</c> argument order when an operand is a matrix, and choosing the sign-correct
/// <c>mod</c> form. When a type genuinely cannot be inferred it returns <see cref="GlslType.Unknown"/>;
/// the emitter then falls back to a conservative (always-correct) form.
/// </summary>
internal sealed class TypeInference
{
    private readonly Dictionary<string, GlslType> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionDecl> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GlslType> _customUniforms = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _aliases;
    private readonly List<Dictionary<string, GlslType>> _scopes = new();

    /// <summary>The predefined ShaderToy uniform types (so references to them infer correctly).</summary>
    private static readonly IReadOnlyDictionary<string, GlslType> Uniforms = BuildUniforms();

    public TypeInference(TranslationUnit unit)
    {
        foreach (GlobalConstDecl g in unit.Globals)
        {
            _globals[g.Name] = TypeTable.Resolve(g.TypeName);
        }

        foreach (FunctionDecl f in unit.Functions)
        {
            _functions[f.Name] = f;
        }

        foreach (CustomUniformDecl cu in unit.CustomUniforms)
        {
            _customUniforms[cu.Name] = cu.IsSampler
                ? GlslType.ScalarOf(ScalarKind.Sampler)
                : TypeTable.Resolve(cu.TypeName);
        }

        // Mutable globals (G1) are file-scope identifiers like const globals; register their types so
        // references infer correctly and are NOT rejected as undeclared.
        foreach (GlobalVarDecl gv in unit.MutableGlobals)
        {
            _globals[gv.Name] = TypeTable.Resolve(gv.TypeName);
        }

        _aliases = unit.Aliases;
    }

    /// <summary>Resolve a glslViewer alias to the ShaderToy built-in it was folded onto (or itself).</summary>
    public string ResolveName(string name) => _aliases.TryGetValue(name, out string? to) ? to : name;

    public void PushScope() => _scopes.Add(new Dictionary<string, GlslType>(StringComparer.Ordinal));

    public void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    public void Declare(string name, string glslType) =>
        _scopes[^1][name] = TypeTable.Resolve(glslType);

    /// <summary>
    /// True if <paramref name="name"/> resolves to a known identifier in the current context: a
    /// declared local/parameter (any enclosing scope), a <c>const</c> global, or a predefined
    /// ShaderToy uniform. Used by the emitter to reject a free (undeclared) identifier at convert
    /// time instead of letting it leak through to a downstream "use of undeclared identifier" error.
    /// </summary>
    public bool IsKnownIdentifier(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].ContainsKey(name))
            {
                return true;
            }
        }

        string resolved = ResolveName(name);
        return _globals.ContainsKey(name) ||
               _customUniforms.ContainsKey(name) ||
               Uniforms.ContainsKey(resolved);
    }

    private GlslType LookupVar(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out GlslType t))
            {
                return t;
            }
        }

        if (_globals.TryGetValue(name, out GlslType g))
        {
            return g;
        }

        if (_customUniforms.TryGetValue(name, out GlslType c))
        {
            return c;
        }

        if (Uniforms.TryGetValue(ResolveName(name), out GlslType u))
        {
            return u;
        }

        return GlslType.Unknown;
    }

    /// <summary>Infer the <see cref="GlslType"/> of an expression (best-effort).</summary>
    public GlslType Infer(Expr expr) => expr switch
    {
        IntLiteralExpr => GlslType.ScalarOf(ScalarKind.Int),
        FloatLiteralExpr => GlslType.ScalarOf(ScalarKind.Float),
        BoolLiteralExpr => GlslType.ScalarOf(ScalarKind.Bool),
        IdentifierExpr id => LookupVar(id.Name),
        SwizzleExpr sw => InferSwizzle(sw),
        IndexExpr idx => InferIndex(idx),
        CallExpr call => InferCall(call),
        UnaryExpr un => un.Op == "!" ? GlslType.ScalarOf(ScalarKind.Bool) : Infer(un.Operand),
        BinaryExpr bin => InferBinary(bin),
        ConditionalExpr c => Merge(Infer(c.WhenTrue), Infer(c.WhenFalse)),
        AssignExpr a => Infer(a.Target),
        _ => GlslType.Unknown,
    };

    private GlslType InferSwizzle(SwizzleExpr sw)
    {
        GlslType target = Infer(sw.Target);
        string m = sw.Member;

        // Matrix `.length()` style is not in scope; a swizzle is component selection.
        if (m.Length is >= 1 and <= 4 && IsSwizzleChars(m))
        {
            ScalarKind scalar = target.IsKnown ? target.Scalar : ScalarKind.Float;
            if (scalar is ScalarKind.Sampler or ScalarKind.Void)
            {
                scalar = ScalarKind.Float;
            }

            return m.Length == 1 ? GlslType.ScalarOf(scalar) : GlslType.Vector(scalar, m.Length);
        }

        return GlslType.Unknown;
    }

    private static bool IsSwizzleChars(string m)
    {
        foreach (char c in m)
        {
            if ("xyzwrgbastpq".IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private GlslType InferIndex(IndexExpr idx)
    {
        GlslType target = Infer(idx.Target);
        if (target.IsMatrix)
        {
            // Indexing a matrix yields a column vector.
            return GlslType.Vector(target.Scalar, target.Rows);
        }

        if (target.IsVector)
        {
            return GlslType.ScalarOf(target.Scalar);
        }

        // iChannelResolution[i] → vec3, iChannelTime[i] → float (target is the array element type
        // already, modeled as the element type in Uniforms below).
        return target;
    }

    private GlslType InferCall(CallExpr call)
    {
        string name = call.Callee;

        // Constructor → the constructed type.
        if (TypeTable.IsTypeName(name))
        {
            return TypeTable.Resolve(name);
        }

        // User function → its declared return type.
        if (_functions.TryGetValue(name, out FunctionDecl? fn))
        {
            return TypeTable.Resolve(fn.ReturnType);
        }

        return InferIntrinsicReturn(name, call.Args);
    }

    private GlslType InferIntrinsicReturn(string name, IReadOnlyList<Expr> args)
    {
        switch (name)
        {
            // texture(samplerN, uv) → vec4.
            case "texture" or "texture2D" or "textureLod" or "textureGrad":
                return GlslType.Vector(ScalarKind.Float, 4);

            // Reductions to a scalar.
            case "length" or "distance" or "dot":
                return GlslType.ScalarOf(ScalarKind.Float);

            // cross → vec3.
            case "cross":
                return GlslType.Vector(ScalarKind.Float, 3);

            // atan/mod and the elementwise math functions return the shape of their first arg.
            default:
                return args.Count > 0 ? PromoteToFloatish(Infer(args[0])) : GlslType.Unknown;
        }
    }

    private static GlslType PromoteToFloatish(GlslType t)
    {
        if (!t.IsKnown)
        {
            return GlslType.Unknown;
        }

        // Most math intrinsics produce float results of the same component count.
        if (t.IsMatrix)
        {
            return t;
        }

        return t.Rows >= 2 ? GlslType.Vector(ScalarKind.Float, t.Rows) : GlslType.ScalarOf(ScalarKind.Float);
    }

    private GlslType InferBinary(BinaryExpr bin)
    {
        // Comparison / logical → bool (scalar in the supported subset).
        if (bin.Op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||")
        {
            return GlslType.ScalarOf(ScalarKind.Bool);
        }

        GlslType l = Infer(bin.Left);
        GlslType r = Infer(bin.Right);

        // Matrix * vector / matrix * matrix → see the emitter for ordering; here we just give shape.
        if (bin.Op == "*")
        {
            if (l.IsMatrix && r.IsVector)
            {
                return GlslType.Vector(r.Scalar, l.Rows);
            }

            if (l.IsVector && r.IsMatrix)
            {
                return GlslType.Vector(l.Scalar, r.Cols);
            }

            if (l.IsMatrix && r.IsMatrix)
            {
                return GlslType.Matrix(l.Rows);
            }
        }

        return Merge(l, r);
    }

    /// <summary>Combine two operand types: prefer the wider/known shape.</summary>
    private static GlslType Merge(GlslType a, GlslType b)
    {
        if (!a.IsKnown)
        {
            return b;
        }

        if (!b.IsKnown)
        {
            return a;
        }

        // Vector wins over scalar; float wins over int for component kind.
        int rows = Math.Max(a.Rows, b.Rows);
        int cols = Math.Max(a.Cols, b.Cols);
        ScalarKind scalar = (a.Scalar == ScalarKind.Float || b.Scalar == ScalarKind.Float)
            ? ScalarKind.Float
            : a.Scalar;
        return new GlslType(scalar, rows, cols);
    }

    private static IReadOnlyDictionary<string, GlslType> BuildUniforms() =>
        new Dictionary<string, GlslType>(StringComparer.Ordinal)
        {
            ["iResolution"] = GlslType.Vector(ScalarKind.Float, 3),
            ["iTime"] = GlslType.ScalarOf(ScalarKind.Float),
            ["iTimeDelta"] = GlslType.ScalarOf(ScalarKind.Float),
            ["iFrame"] = GlslType.ScalarOf(ScalarKind.Int),
            ["iFrameRate"] = GlslType.ScalarOf(ScalarKind.Float),
            ["iMouse"] = GlslType.Vector(ScalarKind.Float, 4),
            ["iDate"] = GlslType.Vector(ScalarKind.Float, 4),
            ["iSampleRate"] = GlslType.ScalarOf(ScalarKind.Float),
            // Array elements modeled as their element type (the index step keeps the same type).
            ["iChannelTime"] = GlslType.ScalarOf(ScalarKind.Float),
            ["iChannelResolution"] = GlslType.Vector(ScalarKind.Float, 3),
            ["iChannel0"] = GlslType.ScalarOf(ScalarKind.Sampler),
            ["iChannel1"] = GlslType.ScalarOf(ScalarKind.Sampler),
            ["iChannel2"] = GlslType.ScalarOf(ScalarKind.Sampler),
            ["iChannel3"] = GlslType.ScalarOf(ScalarKind.Sampler),
        };
}
