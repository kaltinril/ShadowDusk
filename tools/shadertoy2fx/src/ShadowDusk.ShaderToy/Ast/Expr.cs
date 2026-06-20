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

/// <summary>
/// A GLSL array constructor, e.g. <c>float[](a, b, c)</c> or <c>float[3](a, b, c)</c> (G7). The
/// element type is a supported scalar/vector/matrix spelling; HLSL has no array-constructor call
/// syntax, so the emitter renders this as a brace initializer list <c>{ a, b, c }</c> (valid only at
/// a declaration initializer site, which is the only place GLSL array constructors legally appear in
/// the supported subset).
/// </summary>
internal sealed class ArrayConstructorExpr : Expr
{
    public required string ElementTypeName { get; init; }

    /// <summary>The declared length inside <c>[...]</c>, or null for the unsized <c>type[](...)</c> form
    /// (the length is then the element count).</summary>
    public int? DeclaredSize { get; init; }

    public required IReadOnlyList<Expr> Elements { get; init; }
}

/// <summary>
/// A GLSL brace initializer list <c>{ a, b, c }</c> (GLSL ES 3.00+ aggregate initializer), used as an
/// array declaration's initializer (<c>vec2[4] c = { ... };</c>). Distinct from
/// <see cref="ArrayConstructorExpr"/> (the <c>T[](...)</c> call form): a brace list carries no element
/// type token (the declared array type supplies it). The emitter renders it as an HLSL brace list
/// <c>{ a, b, c }</c>, valid at the same declaration-initializer site.
/// </summary>
internal sealed class BraceInitExpr : Expr
{
    public required IReadOnlyList<Expr> Elements { get; init; }
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

/// <summary>
/// A GLSL comma (sequence) expression <c>a, b, c</c>: each sub-expression is evaluated left to right
/// and the value is the last. Appears in <c>for</c> headers (<c>for (...; ...; i++, j--)</c>) and the
/// rare comma expression statement. HLSL has the same operator, so the parts are emitted joined by
/// commas.
/// </summary>
internal sealed class SequenceExpr : Expr
{
    public required IReadOnlyList<Expr> Items { get; init; }
}

/// <summary>An assignment (or compound assignment): <c>x = e</c>, <c>x += e</c>, …</summary>
internal sealed class AssignExpr : Expr
{
    public required string Op { get; init; }
    public required Expr Target { get; init; }
    public required Expr Value { get; init; }
}
