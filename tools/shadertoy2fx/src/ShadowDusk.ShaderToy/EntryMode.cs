using ShadowDusk.ShaderToy.Ast;

namespace ShadowDusk.ShaderToy;

/// <summary>
/// Which entry-point convention a converted shader uses (G2). Detection is by entry-NAME: a shader
/// that defines <c>void mainImage(out vec4, in vec2)</c> is <see cref="ShaderToy"/>; one that defines
/// <c>void main()</c> (no parameters) is <see cref="PlainGlsl"/>. Exactly one must be present (both is
/// an ambiguous reject; neither is the "no entry point" reject).
/// </summary>
internal enum EntryMode
{
    /// <summary>ShaderToy convention: <c>void mainImage(out vec4 fragColor, in vec2 fragCoord)</c>.</summary>
    ShaderToy,

    /// <summary>Plain-GLSL fragment convention: <c>void main()</c> writing <c>gl_FragColor</c> or a
    /// user-declared <c>out vec4 &lt;name&gt;;</c>, reading <c>gl_FragCoord</c>.</summary>
    PlainGlsl,
}

/// <summary>
/// The shape of a <c>mainImage</c> entry (G2). The standard ShaderToy form takes two parameters
/// (<c>out vec4 fragColor, in vec2 fragCoord</c>); GdShaders / Godot 4's port uses a four-... rather a
/// three-parameter form (<c>in vec4 inputColor, in vec2 uv, out vec4 outputColor</c>). Both are valid
/// entries; the harness wires each correctly (see <see cref="HarnessGenerator"/>).
/// </summary>
internal enum MainImageShape
{
    /// <summary>ShaderToy: <c>void mainImage(out vec4 fragColor, in vec2 fragCoord)</c>.</summary>
    Standard,

    /// <summary>Godot/GdShaders: <c>void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)</c>.
    /// <c>uv</c> is Godot's SCREEN_UV ([0,1]) set from the harness (<c>fragCoord/iResolution</c>);
    /// <c>inputColor</c> is the iChannel0 sample at <c>uv</c> (or opaque black if no channel);
    /// <c>outputColor</c> is the returned fragment color.</summary>
    Godot,
}

/// <summary>
/// A tiny read-only AST walker used by entry-point detection: it answers "does this subtree reference
/// a given identifier name anywhere?" (e.g. is <c>gl_FragColor</c> written somewhere in <c>main()</c>).
/// It only walks the supported-subset node shapes, so a new node type would be a compile error here,
/// keeping the scan honest.
/// </summary>
internal static class AstScan
{
    /// <summary>True if any expression in <paramref name="stmt"/> (recursively) names
    /// <paramref name="identifier"/> as a bare identifier or a call head.</summary>
    public static bool MentionsIdentifier(Stmt stmt, string identifier) => stmt switch
    {
        BlockStmt b => b.Statements.Any(s => MentionsIdentifier(s, identifier)),
        VarDeclStmt v => v.Initializer is not null && MentionsIdentifier(v.Initializer, identifier),
        MultiDeclStmt m => m.Declarators.Any(d => MentionsIdentifier(d, identifier)),
        ExprStmt e => MentionsIdentifier(e.Expression, identifier),
        IfStmt i => MentionsIdentifier(i.Condition, identifier) ||
                    MentionsIdentifier(i.Then, identifier) ||
                    (i.Else is not null && MentionsIdentifier(i.Else, identifier)),
        ForStmt f => (f.Init is not null && MentionsIdentifier(f.Init, identifier)) ||
                     (f.Condition is not null && MentionsIdentifier(f.Condition, identifier)) ||
                     (f.Increment is not null && MentionsIdentifier(f.Increment, identifier)) ||
                     MentionsIdentifier(f.Body, identifier),
        WhileStmt w => MentionsIdentifier(w.Condition, identifier) || MentionsIdentifier(w.Body, identifier),
        DoWhileStmt d => MentionsIdentifier(d.Body, identifier) || MentionsIdentifier(d.Condition, identifier),
        ReturnStmt r => r.Value is not null && MentionsIdentifier(r.Value, identifier),
        SwitchStmt sw => MentionsIdentifier(sw.Selector, identifier) ||
                         sw.Cases.Any(c =>
                             c.Labels.Any(l => MentionsIdentifier(l, identifier)) ||
                             c.Body.Any(s => MentionsIdentifier(s, identifier))),
        _ => false, // BreakStmt / ContinueStmt / DiscardStmt: no expressions.
    };

    private static bool MentionsIdentifier(Expr expr, string identifier) => expr switch
    {
        IdentifierExpr id => id.Name == identifier,
        CallExpr c => c.Callee == identifier || c.Args.Any(a => MentionsIdentifier(a, identifier)),
        SwizzleExpr sw => MentionsIdentifier(sw.Target, identifier),
        IndexExpr idx => MentionsIdentifier(idx.Target, identifier) || MentionsIdentifier(idx.Index, identifier),
        ArrayConstructorExpr ac => ac.Elements.Any(e => MentionsIdentifier(e, identifier)),
        UnaryExpr un => MentionsIdentifier(un.Operand, identifier),
        BinaryExpr bin => MentionsIdentifier(bin.Left, identifier) || MentionsIdentifier(bin.Right, identifier),
        ConditionalExpr c => MentionsIdentifier(c.Condition, identifier) ||
                             MentionsIdentifier(c.WhenTrue, identifier) ||
                             MentionsIdentifier(c.WhenFalse, identifier),
        SequenceExpr seq => seq.Items.Any(i => MentionsIdentifier(i, identifier)),
        AssignExpr a => MentionsIdentifier(a.Target, identifier) || MentionsIdentifier(a.Value, identifier),
        _ => false, // literals.
    };
}
