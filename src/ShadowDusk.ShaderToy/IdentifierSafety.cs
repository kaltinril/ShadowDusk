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

    public bool IsEmpty => Global.Count == 0 && LocalsByFunction.Count == 0;
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
    /// these names must be renamed. (GLSL-reserved words can't reach here; intrinsic names like
    /// <c>min</c>/<c>lerp</c> are NOT included — they are not reserved, a variable may shadow them unless
    /// it is also called, which the function-shadow rule covers for user functions.)
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

        // ── Global renames: top-level user functions + const/mutable globals named an HLSL keyword. ──
        foreach (FunctionDecl f in functions)
        {
            if (HlslReserved.Contains(f.Name) && !renames.Global.ContainsKey(f.Name))
            {
                string fresh = Fresh(f.Name, used);
                renames.Global[f.Name] = fresh;
                diagnostics.Add(ReservedWarning("function", f.Name, fresh, f.Line, f.Column));
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

                bool reserved = HlslReserved.Contains(name);
                bool shadowsCalledFunction = userFunctionNames.Contains(name) && calledNames.Contains(name);
                if (!reserved && !shadowsCalledFunction)
                {
                    continue;
                }

                string fresh = Fresh(name, localUsed);
                map ??= new Dictionary<string, string>(StringComparer.Ordinal);
                map[name] = fresh;
                diagnostics.Add(reserved
                    ? ReservedWarning("local", name, fresh, line, column)
                    : ShadowWarning(name, fresh, line, column));
            }

            if (map is not null)
            {
                renames.LocalsByFunction[i] = map;
            }
        }

        return renames;
    }

    private static void AddGlobalReservedRename(
        string name, int line, int column, string kind,
        IdentifierRenames renames, HashSet<string> used, List<ConvertDiagnostic> diagnostics)
    {
        if (!HlslReserved.Contains(name) || renames.Global.ContainsKey(name))
        {
            return;
        }

        string fresh = Fresh(name, used);
        renames.Global[name] = fresh;
        diagnostics.Add(ReservedWarning(kind, name, fresh, line, column));
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
