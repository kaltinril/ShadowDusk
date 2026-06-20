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

    /// <summary>struct name → (member name → member GlslType). Drives member-access inference so a
    /// struct member that is a matrix still routes through the matrix-multiply trap (G6).</summary>
    private readonly Dictionary<string, Dictionary<string, GlslType>> _structs =
        new(StringComparer.Ordinal);

    /// <summary>Array-typed identifiers in the current/global scope → element type. Indexing one yields
    /// the element type (G7). Kept separate from <see cref="GlslType"/> (which has no array shape).</summary>
    private readonly List<Dictionary<string, GlslType>> _arrayScopes = new();
    private readonly Dictionary<string, GlslType> _globalArrays = new(StringComparer.Ordinal);

    /// <summary>The predefined ShaderToy uniform types (so references to them infer correctly).</summary>
    private static readonly IReadOnlyDictionary<string, GlslType> Uniforms = BuildUniforms();

    public TypeInference(TranslationUnit unit)
    {
        // Structs first so member types and struct-typed declarations resolve.
        foreach (StructDecl s in unit.Structs)
        {
            var members = new Dictionary<string, GlslType>(StringComparer.Ordinal);
            foreach (StructMember m in s.Members)
            {
                members[m.Name] = ResolveTypeName(m.TypeName);
            }

            _structs[s.Name] = members;
        }

        foreach (GlobalConstDecl g in unit.Globals)
        {
            if (g.ArraySize is not null)
            {
                _globalArrays[g.Name] = ResolveTypeName(g.TypeName);
            }
            else
            {
                _globals[g.Name] = ResolveTypeName(g.TypeName);
            }
        }

        foreach (FunctionDecl f in unit.Functions)
        {
            _functions[f.Name] = f;
        }

        foreach (CustomUniformDecl cu in unit.CustomUniforms)
        {
            _customUniforms[cu.Name] = cu.IsSampler
                ? GlslType.ScalarOf(ScalarKind.Sampler)
                : ResolveTypeName(cu.TypeName);
        }

        // Mutable globals (G1) are file-scope identifiers like const globals; register their types so
        // references infer correctly and are NOT rejected as undeclared. An array global (G7) registers
        // its element type in the array table instead.
        foreach (GlobalVarDecl gv in unit.MutableGlobals)
        {
            if (gv.ArraySize is not null)
            {
                _globalArrays[gv.Name] = ResolveTypeName(gv.TypeName);
            }
            else
            {
                _globals[gv.Name] = ResolveTypeName(gv.TypeName);
            }
        }

        _aliases = unit.Aliases;
    }

    /// <summary>Resolve a GLSL type spelling, including a user-defined struct name (G6), to a
    /// <see cref="GlslType"/>. A known struct name yields a struct value type; otherwise the built-in
    /// table is used.</summary>
    public GlslType ResolveTypeName(string name) =>
        _structs.ContainsKey(name) ? GlslType.Struct(name) : TypeTable.Resolve(name);

    /// <summary>True if <paramref name="name"/> is a declared user struct type (G6).</summary>
    public bool IsStructType(string name) => _structs.ContainsKey(name);

    /// <summary>Resolve a glslViewer alias to the ShaderToy built-in it was folded onto (or itself).</summary>
    public string ResolveName(string name) => _aliases.TryGetValue(name, out string? to) ? to : name;

    public void PushScope()
    {
        _scopes.Add(new Dictionary<string, GlslType>(StringComparer.Ordinal));
        _arrayScopes.Add(new Dictionary<string, GlslType>(StringComparer.Ordinal));
    }

    public void PopScope()
    {
        _scopes.RemoveAt(_scopes.Count - 1);
        _arrayScopes.RemoveAt(_arrayScopes.Count - 1);
    }

    public void Declare(string name, string glslType) =>
        _scopes[^1][name] = ResolveTypeName(glslType);

    /// <summary>
    /// Register a predefined file-scope builtin global (G2): the plain-GLSL fragment output
    /// (<c>gl_FragColor</c> or the user <c>out vec4 &lt;name&gt;;</c>) and <c>gl_FragCoord</c>, both
    /// <c>vec4</c>. This makes the shader body's references to them resolve as known identifiers (so they
    /// are NOT rejected as undeclared and infer their <c>vec4</c> shape), mirroring how a mutable global
    /// is registered. Only used in <c>void main()</c> mode.
    /// </summary>
    public void DeclareBuiltinGlobal(string name, GlslType type) => _globals[name] = type;

    /// <summary>Register a local array variable's element type (G7), so indexing it infers correctly.</summary>
    public void DeclareArray(string name, string elementGlslType) =>
        _arrayScopes[^1][name] = ResolveTypeName(elementGlslType);

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
            if (_scopes[i].ContainsKey(name) || _arrayScopes[i].ContainsKey(name))
            {
                return true;
            }
        }

        string resolved = ResolveName(name);
        return _globals.ContainsKey(name) ||
               _globalArrays.ContainsKey(name) ||
               _customUniforms.ContainsKey(name) ||
               Uniforms.ContainsKey(resolved);
    }

    /// <summary>Look up the element type of an array-typed identifier (local or global), if any.</summary>
    private bool TryLookupArrayElement(string name, out GlslType element)
    {
        for (int i = _arrayScopes.Count - 1; i >= 0; i--)
        {
            if (_arrayScopes[i].TryGetValue(name, out element))
            {
                return true;
            }
        }

        return _globalArrays.TryGetValue(name, out element);
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
        ArrayConstructorExpr ac => ResolveTypeName(ac.ElementTypeName),
        UnaryExpr un => un.Op == "!" ? GlslType.ScalarOf(ScalarKind.Bool) : Infer(un.Operand),
        BinaryExpr bin => InferBinary(bin),
        ConditionalExpr c => Merge(Infer(c.WhenTrue), Infer(c.WhenFalse)),
        SequenceExpr seq => seq.Items.Count > 0 ? Infer(seq.Items[^1]) : GlslType.Unknown,
        AssignExpr a => Infer(a.Target),
        _ => GlslType.Unknown,
    };

    private GlslType InferSwizzle(SwizzleExpr sw)
    {
        GlslType target = Infer(sw.Target);
        string m = sw.Member;

        // Struct member access (G6): `s.field` infers the member's declared type. This is what lets a
        // matrix-typed member still route through the matrix-multiply trap in the emitter.
        if (target.IsStruct && target.StructName is { } structName &&
            _structs.TryGetValue(structName, out Dictionary<string, GlslType>? members) &&
            members.TryGetValue(m, out GlslType memberType))
        {
            return memberType;
        }

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
        // Indexing an array-typed identifier (G7) yields its element type.
        if (idx.Target is IdentifierExpr arrId && TryLookupArrayElement(arrId.Name, out GlslType elem))
        {
            return elem;
        }

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

        // Struct constructor (G6) → the struct value type.
        if (_structs.ContainsKey(name))
        {
            return GlslType.Struct(name);
        }

        // Constructor → the constructed type.
        if (TypeTable.IsTypeName(name))
        {
            return TypeTable.Resolve(name);
        }

        // User function → its declared return type.
        if (_functions.TryGetValue(name, out FunctionDecl? fn))
        {
            return ResolveTypeName(fn.ReturnType);
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
