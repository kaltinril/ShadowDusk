namespace ShadowDusk.ShaderToy.Ast;

/// <summary>A function parameter with its GLSL parameter qualifier.</summary>
internal sealed class ParamDecl
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    /// <summary>One of <c>in</c> (default), <c>out</c>, <c>inout</c>.</summary>
    public required ParamQualifier Qualifier { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>The direction qualifier on a function parameter.</summary>
internal enum ParamQualifier
{
    In,
    Out,
    InOut,
}

/// <summary>A top-level user-defined function (including <c>mainImage</c>).</summary>
internal sealed class FunctionDecl
{
    public required string ReturnType { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ParamDecl> Parameters { get; init; }
    public required BlockStmt Body { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>A top-level <c>const</c> global declaration.</summary>
internal sealed class GlobalConstDecl
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    public required Expr Initializer { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>The parsed translation unit: globals + functions, in source order within each list.</summary>
internal sealed class TranslationUnit
{
    public required IReadOnlyList<GlobalConstDecl> Globals { get; init; }
    public required IReadOnlyList<FunctionDecl> Functions { get; init; }
}
