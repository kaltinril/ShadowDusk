using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// The computed identifier renames for one shader (F1): which top-level and per-function-local names
/// must be renamed so the emitted HLSL is valid, even though the source GLSL was. Empty when the shader
/// has no collisions (the overwhelmingly common case), so it changes nothing for a clean shader.
/// </summary>
internal sealed class IdentifierRenames
{
    /// <summary>Top-level user identifiers (functions, const/mutable globals) renamed because their name
    /// is an HLSL reserved keyword. Applied to the declaration AND every reference (value + call).</summary>
    public Dictionary<string, string> Global { get; } = new(StringComparer.Ordinal);

    /// <summary>Per-function local/parameter renames, keyed by the function index in the emitted order.
    /// A local is renamed when its name is an HLSL reserved keyword, or it shadows a user function that
    /// is actually CALLED in the body (HLSL, unlike GLSL, then reads the call as "call the variable").
    /// Applied to the declaration and the local's VALUE references; call heads stay bound to the
    /// function.</summary>
    public Dictionary<int, Dictionary<string, string>> LocalsByFunction { get; } = new();

    /// <summary>Per-for-loop init-variable renames (legacy HLSL for-scope leak). GLSL scopes a for-loop's
    /// variable to its own loop, so a function may reuse the same name across sibling/nested loops; HLSL
    /// instead leaks the for-init into the enclosing scope, so the reuse is a <c>-Wfor-redefinition</c>
    /// error (under <c>-WX</c>) regardless of type. The second-and-later loops to use a name get a fresh
    /// one. Keyed by the loop node so the emitter applies the rename to that loop's init + body ONLY (the
    /// name keeps its meaning everywhere else). Safe because a GLSL for-init is never read after its
    /// loop.</summary>
    public Dictionary<ForStmt, Dictionary<string, string>> ForLoopLocals { get; } =
        new(ReferenceEqualityComparer.Instance);

    public bool IsEmpty => Global.Count == 0 && LocalsByFunction.Count == 0 && ForLoopLocals.Count == 0;
}

/// <summary>
/// F1 (Phase 47) identifier-safety pass. GLSL allows constructs HLSL does not: a local variable may
/// shadow a function name and still call the function (<c>mat3 rot = rot(a, b);</c>), and identifiers may
/// collide with HLSL reserved keywords (<c>matrix</c>, <c>sample</c>, <c>linear</c>, ...). The converter
/// used to pass these through verbatim, so DXC rejected the emitted HLSL with an opaque, mislocated error.
/// This pass detects them up front and plans a faithful RENAME (the declaration plus its references),
/// emitting a located <see cref="DiagnosticSeverity.Warning"/> for each, so a shader that is valid GLSL
/// compiles instead of failing on generated HLSL. It renames ONLY where HLSL genuinely breaks, so it
/// never changes the output of a shader that already compiled.
/// </summary>
internal static class IdentifierSafety
{
    /// <summary>
    /// HLSL reserved keywords / type-modifiers that are valid GLSL identifiers and so can legally appear
    /// as a name in a ShaderToy shader, but are NOT valid HLSL identifiers. A declaration with one of
    /// these names must be renamed. (GLSL-reserved words can't reach here; same-name intrinsics like
    /// <c>min</c> are NOT included — they are not reserved, and shadowing one breaks only where the
    /// GLSL was already broken. The names the CONVERTER ITSELF introduces are the separate
    /// <see cref="EmitterIntroducedNames"/> set below.)
    /// </summary>
    private static readonly HashSet<string> HlslReserved = new(StringComparer.Ordinal)
    {
        "matrix", "vector", "sample", "linear", "centroid", "nointerpolation", "noperspective",
        "precise", "shared", "groupshared", "globallycoherent", "technique", "technique10",
        "technique11", "pass", "texture", "sampler_state", "stateblock", "stateblock_state",
        "register", "packoffset", "cbuffer", "tbuffer", "string", "compile", "compile_fragment",
        "row_major", "column_major", "snorm", "unorm", "dword", "half", "fixed", "interface",
        "class", "namespace", "pixelshader", "vertexshader", "pixelfragment", "vertexfragment",
        "asm", "asm_fragment", "typedef", "template", "this", "inline",
    };

    /// <summary>
    /// Names that are NOT HLSL-reserved but that the converter's OWN OUTPUT references: the HLSL
    /// intrinsics the intrinsic renames target (<c>fract</c>→<c>frac</c>, <c>mix</c>→<c>lerp</c>,
    /// <c>texture</c>→<c>tex2D</c>, 2-arg <c>atan</c>→<c>atan2</c>, …) and the symbols the harness /
    /// emitter synthesizes. A user identifier with one of these names is valid GLSL AND valid plain
    /// HLSL, but HLSL resolves a call against the nearest declaration, so it would capture the
    /// converter-introduced references (e.g. <c>float frac = fract(x);</c> emits
    /// <c>float frac = frac(x);</c> — "call the variable"). Renamed exactly like a reserved word.
    /// </summary>
    private static readonly HashSet<string> EmitterIntroducedNames = new(StringComparer.Ordinal)
    {
        // HLSL intrinsics the rename table / special-case rewrites emit:
        "frac", "lerp", "tex2D", "tex2Dlod", "tex2Dgrad", "atan2", "rsqrt", "ddx", "ddy",
        "fmod", "saturate", "mad",
        // Symbols the generated harness declares around the translated body:
        "PSMain", "VSMain", "VSInput", "VSOutput", "glsl_mod", "sd_ScreenUV",
    };

    /// <summary>True when a user identifier must be renamed regardless of how it is used: an HLSL
    /// reserved word, or a name the converter's own output references.</summary>
    private static bool IsUnsafeName(string name) =>
        HlslReserved.Contains(name) || EmitterIntroducedNames.Contains(name);

    /// <summary>
    /// Plan the renames for the merged translation unit. <paramref name="diagnostics"/> receives a located
    /// Warning per rename. Returns an empty plan for a shader with no collisions.
    /// </summary>
    public static IdentifierRenames Plan(
        TranslationUnit unit,
        IReadOnlyList<FunctionDecl> functions,
        List<ConvertDiagnostic> diagnostics)
    {
        var renames = new IdentifierRenames();

        var userFunctionNames = new HashSet<string>(functions.Select(f => f.Name), StringComparer.Ordinal);

        // Master set of names to avoid when minting a fresh replacement (so a rename never collides with
        // an existing identifier). Seeded with every top-level name + the reserved set + the ShaderToy
        // built-ins; per-function local names are added when renaming that function's locals.
        var used = new HashSet<string>(StringComparer.Ordinal);
        used.UnionWith(userFunctionNames);
        used.UnionWith(unit.Globals.Select(g => g.Name));
        used.UnionWith(unit.MutableGlobals.Select(g => g.Name));
        used.UnionWith(unit.CustomUniforms.Select(c => c.Name));
        used.UnionWith(unit.Structs.Select(s => s.Name));
        used.UnionWith(HlslReserved);
        used.UnionWith(EmitterIntroducedNames);

        // ── Global renames: top-level user functions + const/mutable globals whose name is an HLSL
        //    keyword or a name the converter's output uses (an intrinsic rename target / harness symbol). ──
        foreach (FunctionDecl f in functions)
        {
            if (IsUnsafeName(f.Name) && !renames.Global.ContainsKey(f.Name))
            {
                string fresh = Fresh(f.Name, used);
                renames.Global[f.Name] = fresh;
                diagnostics.Add(UnsafeNameWarning("function", f.Name, fresh, f.Line, f.Column));
            }
        }

        foreach (GlobalConstDecl g in unit.Globals)
        {
            AddGlobalReservedRename(g.Name, g.Line, g.Column, "global", renames, used, diagnostics);
        }

        foreach (GlobalVarDecl g in unit.MutableGlobals)
        {
            AddGlobalReservedRename(g.Name, g.Line, g.Column, "global", renames, used, diagnostics);
        }

        // ── Per-function local renames. ──
        for (int i = 0; i < functions.Count; i++)
        {
            FunctionDecl f = functions[i];

            var calledNames = new HashSet<string>(StringComparer.Ordinal);
            CollectCalledNames(f.Body, calledNames);

            var localDecls = new List<(string Name, int Line, int Column)>();
            foreach (ParamDecl p in f.Parameters)
            {
                localDecls.Add((p.Name, p.Line, p.Column));
            }

            CollectLocalDecls(f.Body, localDecls);

            Dictionary<string, string>? map = null;
            // Avoid colliding a fresh local name with any other local in this function.
            var localUsed = new HashSet<string>(used, StringComparer.Ordinal);
            localUsed.UnionWith(localDecls.Select(d => d.Name));

            foreach ((string name, int line, int column) in localDecls)
            {
                if (map is not null && map.ContainsKey(name))
                {
                    continue; // already renamed in this function (block-shadowed re-declaration)
                }

                bool unsafeName = IsUnsafeName(name);
                bool shadowsCalledFunction = userFunctionNames.Contains(name) && calledNames.Contains(name);
                if (!unsafeName && !shadowsCalledFunction)
                {
                    continue;
                }

                string fresh = Fresh(name, localUsed);
                map ??= new Dictionary<string, string>(StringComparer.Ordinal);
                map[name] = fresh;
                diagnostics.Add(unsafeName
                    ? UnsafeNameWarning("local", name, fresh, line, column)
                    : ShadowWarning(name, fresh, line, column));
            }

            if (map is not null)
            {
                renames.LocalsByFunction[i] = map;
            }

            // Legacy HLSL for-scope leak (independent of the reserved/shadow renames above): a function may
            // reuse a for-loop's variable across loops (valid GLSL — each loop scopes its own variable), but
            // HLSL leaks the for-init into the enclosing scope, so the reuse is a -Wfor-redefinition error.
            // Rename the second-and-later loops' variable. localUsed already holds every local name; add the
            // F1 rename targets too so a fresh name never collides with one.
            localUsed.UnionWith(renames.Global.Values);
            if (map is not null)
            {
                localUsed.UnionWith(map.Values);
            }

            PlanForLoopScopes(f.Body, new HashSet<string>(StringComparer.Ordinal), localUsed, renames, diagnostics);
        }

        return renames;
    }

    /// <summary>
    /// Walk a function body in emit order, renaming each for-loop init variable whose name a PRIOR for-loop
    /// in the same function already declared. <paramref name="seenForInits"/> accumulates the for-init names
    /// (and minted replacements) already used; <paramref name="mintUsed"/> is the master set a fresh name
    /// must avoid.
    /// </summary>
    private static void PlanForLoopScopes(
        Stmt stmt,
        HashSet<string> seenForInits,
        HashSet<string> mintUsed,
        IdentifierRenames renames,
        List<ConvertDiagnostic> diagnostics)
    {
        switch (stmt)
        {
            case BlockStmt b:
                foreach (Stmt s in b.Statements) PlanForLoopScopes(s, seenForInits, mintUsed, renames, diagnostics);
                break;
            case IfStmt i:
                PlanForLoopScopes(i.Then, seenForInits, mintUsed, renames, diagnostics);
                if (i.Else is not null) PlanForLoopScopes(i.Else, seenForInits, mintUsed, renames, diagnostics);
                break;
            case ForStmt f:
                // Process THIS loop's init declarations before its body (matches the emitter's order, so the
                // outer/earlier loop wins the original name and inner/later loops are the ones renamed).
                PlanForInit(f, seenForInits, mintUsed, renames, diagnostics);
                PlanForLoopScopes(f.Body, seenForInits, mintUsed, renames, diagnostics);
                break;
            case WhileStmt w:
                PlanForLoopScopes(w.Body, seenForInits, mintUsed, renames, diagnostics);
                break;
            case DoWhileStmt d:
                PlanForLoopScopes(d.Body, seenForInits, mintUsed, renames, diagnostics);
                break;
            case SwitchStmt sw:
                foreach (SwitchCase c in sw.Cases)
                {
                    foreach (Stmt s in c.Body) PlanForLoopScopes(s, seenForInits, mintUsed, renames, diagnostics);
                }

                break;
        }
    }

    /// <summary>Plan the rename (if any) for one for-loop's init variable(s).</summary>
    private static void PlanForInit(
        ForStmt f,
        HashSet<string> seenForInits,
        HashSet<string> mintUsed,
        IdentifierRenames renames,
        List<ConvertDiagnostic> diagnostics)
    {
        IReadOnlyList<VarDeclStmt> decls = f.Init switch
        {
            VarDeclStmt vd => new[] { vd },
            MultiDeclStmt md => md.Declarators,
            _ => Array.Empty<VarDeclStmt>(), // an expression init declares nothing
        };

        Dictionary<string, string>? map = null;
        foreach (VarDeclStmt d in decls)
        {
            if (seenForInits.Add(d.Name))
            {
                continue; // first for-loop in this function to use this name: it keeps the original.
            }

            string fresh = Fresh(d.Name, mintUsed);
            seenForInits.Add(fresh);
            map ??= new Dictionary<string, string>(StringComparer.Ordinal);
            map[d.Name] = fresh;
            diagnostics.Add(ForScopeWarning(d.Name, fresh, d.Line, d.Column));
        }

        if (map is not null)
        {
            renames.ForLoopLocals[f] = map;
        }
    }

    private static void AddGlobalReservedRename(
        string name, int line, int column, string kind,
        IdentifierRenames renames, HashSet<string> used, List<ConvertDiagnostic> diagnostics)
    {
        if (!IsUnsafeName(name) || renames.Global.ContainsKey(name))
        {
            return;
        }

        string fresh = Fresh(name, used);
        renames.Global[name] = fresh;
        diagnostics.Add(UnsafeNameWarning(kind, name, fresh, line, column));
    }

    private static string Fresh(string name, HashSet<string> used)
    {
        string candidate = name + "_sd";
        int n = 2;
        while (used.Contains(candidate))
        {
            candidate = name + "_sd" + n;
            n++;
        }

        used.Add(candidate);
        return candidate;
    }

    /// <summary>Pick the right warning wording for an unconditionally-renamed name: reserved word vs.
    /// converter-introduced intrinsic/harness collision.</summary>
    private static ConvertDiagnostic UnsafeNameWarning(string kind, string name, string fresh, int line, int col) =>
        HlslReserved.Contains(name)
            ? ReservedWarning(kind, name, fresh, line, col)
            : CollisionWarning(kind, name, fresh, line, col);

    private static ConvertDiagnostic CollisionWarning(string kind, string name, string fresh, int line, int col) =>
        new(DiagnosticSeverity.Warning,
            $"'{name}' collides with an HLSL intrinsic or generated harness symbol the converted " +
            $"shader itself uses; renamed the {kind} to '{fresh}' so the converter-introduced " +
            "references still resolve. (Valid in GLSL, but the generated HLSL would bind them to " +
            "your declaration instead.)",
            line, col, name);

    private static ConvertDiagnostic ReservedWarning(string kind, string name, string fresh, int line, int col) =>
        new(DiagnosticSeverity.Warning,
            $"'{name}' is a reserved word in HLSL; renamed the {kind} to '{fresh}' so the converted .fx " +
            "compiles. (Valid in GLSL, but not a usable HLSL identifier.)",
            line, col, name);

    private static ConvertDiagnostic ShadowWarning(string name, string fresh, int line, int col) =>
        new(DiagnosticSeverity.Warning,
            $"The local '{name}' shadows the function '{name}', which it also calls. HLSL (unlike GLSL) " +
            $"then reads the call as 'call the variable'; renamed the local to '{fresh}' so the call still " +
            "resolves to the function.",
            line, col, name);

    private static ConvertDiagnostic ForScopeWarning(string name, string fresh, int line, int col) =>
        new(DiagnosticSeverity.Warning,
            $"The for-loop variable '{name}' is also used by an earlier loop in this function. GLSL scopes " +
            $"each loop's variable to its own loop, but HLSL leaks it into the enclosing scope (so reusing " +
            $"the name is a redefinition error); renamed this loop's '{name}' to '{fresh}'. Your GLSL is " +
            "valid; this is an internal rename so the converted .fx compiles.",
            line, col, name);

    // ── read-only collection walks (kept local so AstScan's entry-detection stays untouched) ──

    private static void CollectCalledNames(Stmt stmt, HashSet<string> into)
    {
        switch (stmt)
        {
            case BlockStmt b: foreach (Stmt s in b.Statements) CollectCalledNames(s, into); break;
            case VarDeclStmt v: if (v.Initializer is not null) CollectCalledNames(v.Initializer, into); break;
            case MultiDeclStmt m: foreach (VarDeclStmt d in m.Declarators) CollectCalledNames(d, into); break;
            case ExprStmt e: CollectCalledNames(e.Expression, into); break;
            case IfStmt i:
                CollectCalledNames(i.Condition, into);
                CollectCalledNames(i.Then, into);
                if (i.Else is not null) CollectCalledNames(i.Else, into);
                break;
            case ForStmt f:
                if (f.Init is not null) CollectCalledNames(f.Init, into);
                if (f.Condition is not null) CollectCalledNames(f.Condition, into);
                if (f.Increment is not null) CollectCalledNames(f.Increment, into);
                CollectCalledNames(f.Body, into);
                break;
            case WhileStmt w: CollectCalledNames(w.Condition, into); CollectCalledNames(w.Body, into); break;
            case DoWhileStmt d: CollectCalledNames(d.Body, into); CollectCalledNames(d.Condition, into); break;
            case ReturnStmt r: if (r.Value is not null) CollectCalledNames(r.Value, into); break;
            case SwitchStmt sw:
                CollectCalledNames(sw.Selector, into);
                foreach (SwitchCase c in sw.Cases)
                {
                    foreach (Expr l in c.Labels) CollectCalledNames(l, into);
                    foreach (Stmt s in c.Body) CollectCalledNames(s, into);
                }

                break;
        }
    }

    private static void CollectCalledNames(Expr expr, HashSet<string> into)
    {
        switch (expr)
        {
            case CallExpr c:
                into.Add(c.Callee);
                foreach (Expr a in c.Args) CollectCalledNames(a, into);
                break;
            case SwizzleExpr sw: CollectCalledNames(sw.Target, into); break;
            case IndexExpr idx: CollectCalledNames(idx.Target, into); CollectCalledNames(idx.Index, into); break;
            case ArrayConstructorExpr ac: foreach (Expr e in ac.Elements) CollectCalledNames(e, into); break;
            case BraceInitExpr bi: foreach (Expr e in bi.Elements) CollectCalledNames(e, into); break;
            case UnaryExpr un: CollectCalledNames(un.Operand, into); break;
            case BinaryExpr bin: CollectCalledNames(bin.Left, into); CollectCalledNames(bin.Right, into); break;
            case ConditionalExpr c:
                CollectCalledNames(c.Condition, into);
                CollectCalledNames(c.WhenTrue, into);
                CollectCalledNames(c.WhenFalse, into);
                break;
            case SequenceExpr seq: foreach (Expr i in seq.Items) CollectCalledNames(i, into); break;
            case AssignExpr a: CollectCalledNames(a.Target, into); CollectCalledNames(a.Value, into); break;
        }
    }

    private static void CollectLocalDecls(Stmt stmt, List<(string, int, int)> into)
    {
        switch (stmt)
        {
            case BlockStmt b: foreach (Stmt s in b.Statements) CollectLocalDecls(s, into); break;
            case VarDeclStmt v: into.Add((v.Name, v.Line, v.Column)); break;
            case MultiDeclStmt m: foreach (VarDeclStmt d in m.Declarators) into.Add((d.Name, d.Line, d.Column)); break;
            case IfStmt i:
                CollectLocalDecls(i.Then, into);
                if (i.Else is not null) CollectLocalDecls(i.Else, into);
                break;
            case ForStmt f:
                if (f.Init is not null) CollectLocalDecls(f.Init, into);
                CollectLocalDecls(f.Body, into);
                break;
            case WhileStmt w: CollectLocalDecls(w.Body, into); break;
            case DoWhileStmt d: CollectLocalDecls(d.Body, into); break;
            case SwitchStmt sw:
                foreach (SwitchCase c in sw.Cases)
                {
                    foreach (Stmt s in c.Body) CollectLocalDecls(s, into);
                }

                break;
        }
    }
}
