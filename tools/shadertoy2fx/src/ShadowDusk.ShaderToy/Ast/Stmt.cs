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

/// <summary>A <c>break;</c>.</summary>
internal sealed class BreakStmt : Stmt;

/// <summary>A <c>continue;</c>.</summary>
internal sealed class ContinueStmt : Stmt;

/// <summary>A <c>discard;</c>.</summary>
internal sealed class DiscardStmt : Stmt;
