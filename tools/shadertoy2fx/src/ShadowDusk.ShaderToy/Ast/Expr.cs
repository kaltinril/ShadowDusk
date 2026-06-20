namespace ShadowDusk.ShaderToy.Ast;

/// <summary>Base class for all GLSL expression AST nodes. Carries source position for diagnostics.</summary>
internal abstract class Expr
{
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>An integer literal, e.g. <c>42</c> or <c>0xFF</c>.</summary>
internal sealed class IntLiteralExpr : Expr
{
    public required string Text { get; init; }
}

/// <summary>A floating-point literal, e.g. <c>1.0</c>, <c>.5</c>, <c>2e3</c>, <c>1.5f</c>.</summary>
internal sealed class FloatLiteralExpr : Expr
{
    public required string Text { get; init; }
}

/// <summary>A boolean literal, <c>true</c> or <c>false</c>.</summary>
internal sealed class BoolLiteralExpr : Expr
{
    public required bool Value { get; init; }
}

/// <summary>A bare identifier reference (variable, uniform, or function name in a call head).</summary>
internal sealed class IdentifierExpr : Expr
{
    public required string Name { get; init; }
}

/// <summary>A swizzle / member access, e.g. <c>v.xyz</c> or <c>color.rgb</c>.</summary>
internal sealed class SwizzleExpr : Expr
{
    public required Expr Target { get; init; }
    public required string Member { get; init; }
}

/// <summary>An array / vector index, e.g. <c>arr[i]</c> or <c>iChannelResolution[0]</c>.</summary>
internal sealed class IndexExpr : Expr
{
    public required Expr Target { get; init; }
    public required Expr Index { get; init; }
}

/// <summary>
/// A call expression. Doubles as a constructor when <see cref="Callee"/> names a type
/// (e.g. <c>vec3(1.0)</c>) — the type-inference / emitter layers disambiguate.
/// </summary>
internal sealed class CallExpr : Expr
{
    public required string Callee { get; init; }
    public required IReadOnlyList<Expr> Args { get; init; }
}

/// <summary>A unary prefix expression: <c>-x</c>, <c>!b</c>, <c>+x</c>, <c>++i</c>, <c>--i</c>.</summary>
internal sealed class UnaryExpr : Expr
{
    public required string Op { get; init; }
    public required Expr Operand { get; init; }
    /// <summary>True for <c>i++</c>/<c>i--</c> (postfix), false for prefix forms.</summary>
    public bool IsPostfix { get; init; }
}

/// <summary>A binary expression (arithmetic, relational, logical, bitwise).</summary>
internal sealed class BinaryExpr : Expr
{
    public required string Op { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }
}

/// <summary>A ternary conditional <c>cond ? a : b</c>.</summary>
internal sealed class ConditionalExpr : Expr
{
    public required Expr Condition { get; init; }
    public required Expr WhenTrue { get; init; }
    public required Expr WhenFalse { get; init; }
}

/// <summary>An assignment (or compound assignment): <c>x = e</c>, <c>x += e</c>, …</summary>
internal sealed class AssignExpr : Expr
{
    public required string Op { get; init; }
    public required Expr Target { get; init; }
    public required Expr Value { get; init; }
}
