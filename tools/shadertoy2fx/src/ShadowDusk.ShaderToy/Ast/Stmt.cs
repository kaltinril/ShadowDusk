namespace ShadowDusk.ShaderToy.Ast;

/// <summary>Base class for all GLSL statement AST nodes.</summary>
internal abstract class Stmt
{
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>A brace-delimited block <c>{ … }</c>.</summary>
internal sealed class BlockStmt : Stmt
{
    public required IReadOnlyList<Stmt> Statements { get; init; }
}

/// <summary>A local variable declaration (optionally with an initializer), e.g. <c>float x = 1.0;</c>.</summary>
internal sealed class VarDeclStmt : Stmt
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    public Expr? Initializer { get; init; }
    /// <summary>True for a <c>const</c> local.</summary>
    public bool IsConst { get; init; }

    /// <summary>
    /// The fixed array length when this is a local fixed-size array (<c>float arr[4];</c> or
    /// <c>const float k[3] = float[](...);</c>), or null for a scalar/vector/matrix/struct local. (G7.)
    /// </summary>
    public int? ArraySize { get; init; }
}

/// <summary>
/// A comma-separated multi-declarator declaration, e.g. <c>float c = cos(a), s = sin(a);</c>.
/// Unlike a <see cref="BlockStmt"/>, this does NOT introduce a new scope — the declarators are
/// siblings in the enclosing block and must be emitted as such (no braces).
/// </summary>
internal sealed class MultiDeclStmt : Stmt
{
    public required IReadOnlyList<VarDeclStmt> Declarators { get; init; }
}

/// <summary>An expression used as a statement, e.g. <c>x = 1.0;</c> or <c>f();</c>.</summary>
internal sealed class ExprStmt : Stmt
{
    public required Expr Expression { get; init; }
}

/// <summary>An <c>if</c> / optional <c>else</c>.</summary>
internal sealed class IfStmt : Stmt
{
    public required Expr Condition { get; init; }
    public required Stmt Then { get; init; }
    public Stmt? Else { get; init; }
}

/// <summary>A C-style <c>for</c> loop.</summary>
internal sealed class ForStmt : Stmt
{
    public Stmt? Init { get; init; }
    public Expr? Condition { get; init; }
    public Expr? Increment { get; init; }
    public required Stmt Body { get; init; }
}

/// <summary>A <c>while</c> loop.</summary>
internal sealed class WhileStmt : Stmt
{
    public required Expr Condition { get; init; }
    public required Stmt Body { get; init; }
}

/// <summary>A <c>do … while</c> loop.</summary>
internal sealed class DoWhileStmt : Stmt
{
    public required Stmt Body { get; init; }
    public required Expr Condition { get; init; }
}

/// <summary>A <c>return</c> (optionally with a value).</summary>
internal sealed class ReturnStmt : Stmt
{
    public Expr? Value { get; init; }
}

/// <summary>
/// A <c>switch (selector) { case K: ...; ... }</c> statement (lowered to an if/else-if chain by the
/// emitter so it is portable to SM3 / FNA fx_2_0, which have no native <c>switch</c>). Each
/// <see cref="SwitchCase"/> carries one or more <c>case</c> label values (multiple labels sharing one
/// body), or is the <c>default</c>. A case body that is non-empty and does NOT end in
/// <c>break</c>/<c>return</c> (true C fall-through into the next case) is rejected at parse time, since
/// faithfully lowering fall-through is error-prone; an empty body BEFORE a labelled body (shared labels)
/// is supported.
/// </summary>
internal sealed class SwitchStmt : Stmt
{
    /// <summary>The selector expression (<c>switch (e)</c>); compared for equality against each label.</summary>
    public required Expr Selector { get; init; }

    /// <summary>The cases in source order. At most one is the <c>default</c>.</summary>
    public required IReadOnlyList<SwitchCase> Cases { get; init; }
}

/// <summary>
/// One arm of a <see cref="SwitchStmt"/>: the <c>case</c> label value(s) it matches (empty when this is
/// the <c>default</c>), and the statements of its body (with the terminating <c>break</c> already
/// stripped). Multiple labels stacked on one body (<c>case 1: case 2: ...</c>) are collected into
/// <see cref="Labels"/>.
/// </summary>
internal sealed class SwitchCase
{
    /// <summary>The <c>case</c> label value expressions this arm matches; empty for <c>default</c>.</summary>
    public required IReadOnlyList<Expr> Labels { get; init; }

    /// <summary>True when this arm is the <c>default</c> case.</summary>
    public required bool IsDefault { get; init; }

    /// <summary>The arm's body statements (the trailing <c>break;</c> is not included).</summary>
    public required IReadOnlyList<Stmt> Body { get; init; }
}

/// <summary>A <c>break;</c>.</summary>
internal sealed class BreakStmt : Stmt;

/// <summary>A <c>continue;</c>.</summary>
internal sealed class ContinueStmt : Stmt;

/// <summary>A <c>discard;</c>.</summary>
internal sealed class DiscardStmt : Stmt;
