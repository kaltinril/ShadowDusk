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

/// <summary>
/// A top-level custom <c>uniform</c> declaration the consumer drives as an effect parameter, e.g.
/// <c>uniform float u_roughness;</c> or <c>uniform sampler2D u_noise;</c>. Distinct from the
/// predefined ShaderToy uniforms (which the harness injects) and from a redundant built-in
/// re-declaration (which is dropped): a custom uniform is emitted as its own HLSL global / sampler.
/// </summary>
internal sealed class CustomUniformDecl
{
    /// <summary>The declared GLSL type spelling (e.g. <c>float</c>, <c>vec3</c>, <c>mat3</c>).
    /// For a sampler this is the GLSL sampler spelling (<c>sampler2D</c>).</summary>
    public required string TypeName { get; init; }

    /// <summary>The uniform's name (the effect-parameter name the consumer sets).</summary>
    public required string Name { get; init; }

    /// <summary>True when this is a <c>sampler2D</c> (emitted as a texture + sampler_state pair).</summary>
    public required bool IsSampler { get; init; }

    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>The parsed translation unit: globals + functions, in source order within each list.</summary>
internal sealed class TranslationUnit
{
    public required IReadOnlyList<GlobalConstDecl> Globals { get; init; }
    public required IReadOnlyList<FunctionDecl> Functions { get; init; }

    /// <summary>Top-level custom <c>uniform</c> declarations the consumer drives.</summary>
    public IReadOnlyList<CustomUniformDecl> CustomUniforms { get; init; } = Array.Empty<CustomUniformDecl>();

    /// <summary>glslViewer-style alias name → ShaderToy built-in it was folded onto (e.g. u_time → iTime),
    /// applied to identifier references so a declared alias resolves to its built-in.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
