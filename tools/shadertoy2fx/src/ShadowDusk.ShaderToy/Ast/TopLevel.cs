namespace ShadowDusk.ShaderToy.Ast;

/// <summary>A function parameter with its GLSL parameter qualifier.</summary>
internal sealed class ParamDecl
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    /// <summary>One of <c>in</c> (default), <c>out</c>, <c>inout</c>.</summary>
    public required ParamQualifier Qualifier { get; init; }

    /// <summary>
    /// The fixed array length when this is an array parameter (<c>void f(inout float[9] k)</c> or
    /// <c>void f(float k[9])</c>), or null for a scalar/vector/matrix/struct parameter. (G7c.) HLSL
    /// requires the size on the declarator name, so the emitter spells it as <c>T name[N]</c>.
    /// </summary>
    public int? ArraySize { get; init; }

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

    /// <summary>
    /// The fixed array length when this is a <c>const</c> array (<c>const float k[3] = float[](...);</c>),
    /// or null for a scalar/vector/matrix const. (G7.)
    /// </summary>
    public int? ArraySize { get; init; }

    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>
/// A top-level non-<c>const</c> mutable global variable (GLSL fragment-scope global), e.g.
/// <c>float g;</c> or <c>vec2 p = vec2(0.0);</c>. GLSL fragment globals are per-invocation mutable
/// state; the converter emits each as an HLSL <c>static</c> global, which has the matching
/// per-invocation-mutable semantics. Multiple declarators of one statement (<c>float a, b = 1.0;</c>)
/// become one <see cref="GlobalVarDecl"/> each.
/// </summary>
internal sealed class GlobalVarDecl
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }

    /// <summary>The optional initializer (null for a bare <c>float g;</c>).</summary>
    public Expr? Initializer { get; init; }

    /// <summary>The fixed array length for a top-level array global (<c>float k[3];</c>), or null. (G7.)</summary>
    public int? ArraySize { get; init; }

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

    /// <summary>
    /// The optional default value (GLSL 1.20+ allows <c>uniform float x = 1.0;</c>). When present it is
    /// emitted as the HLSL parameter's default initializer, so the consumer gets that value unless they
    /// override it. Null for a plain <c>uniform float x;</c>. Never set for a sampler.
    /// </summary>
    public Expr? Initializer { get; init; }

    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>
/// A top-level user-declared fragment output of a plain-GLSL <c>void main()</c> shader (G2):
/// <c>out vec4 outColor;</c> or <c>layout(location = 0) out vec4 outColor;</c> (GLSL ES 3.00 / desktop
/// 330). This is NOT a custom uniform and is NOT emitted as a global/parameter — it names the local the
/// synthesized pixel shader returns as its <c>COLOR0</c>. The legacy <c>gl_FragColor</c> write target
/// needs no declaration, so it has no <see cref="FragmentOutputDecl"/>.
/// </summary>
internal sealed class FragmentOutputDecl
{
    /// <summary>The declared output variable's name (e.g. <c>outColor</c>).</summary>
    public required string Name { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>One member of a user-defined <c>struct</c>: its GLSL type spelling and field name.</summary>
internal sealed class StructMember
{
    public required string TypeName { get; init; }
    public required string Name { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>
/// A top-level user-defined <c>struct Name { type member; ... };</c> (G6). HLSL has near-identical
/// struct syntax, so the converter emits an HLSL <c>struct</c> with the member types re-spelled. A
/// GLSL struct constructor call <c>Name(a, b)</c> has no direct HLSL equivalent, so the converter
/// generates a factory function <c>Name make_Name(...)</c> and rewrites the constructor to call it.
/// </summary>
internal sealed class StructDecl
{
    public required string Name { get; init; }
    public required IReadOnlyList<StructMember> Members { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>The parsed translation unit: globals + functions, in source order within each list.</summary>
internal sealed class TranslationUnit
{
    public required IReadOnlyList<GlobalConstDecl> Globals { get; init; }
    public required IReadOnlyList<FunctionDecl> Functions { get; init; }

    /// <summary>
    /// Top-level user-declared fragment output(s) of a plain-GLSL <c>void main()</c> shader (G2):
    /// <c>out vec4 outColor;</c> / <c>layout(location=0) out vec4 outColor;</c>. Empty for a ShaderToy
    /// <c>mainImage</c> shader (or a <c>main()</c> shader that writes the legacy <c>gl_FragColor</c>).
    /// </summary>
    public IReadOnlyList<FragmentOutputDecl> FragmentOutputs { get; init; } = Array.Empty<FragmentOutputDecl>();

    /// <summary>Top-level user-defined <c>struct</c> declarations (G6), in source order.</summary>
    public IReadOnlyList<StructDecl> Structs { get; init; } = Array.Empty<StructDecl>();

    /// <summary>Top-level custom <c>uniform</c> declarations the consumer drives.</summary>
    public IReadOnlyList<CustomUniformDecl> CustomUniforms { get; init; } = Array.Empty<CustomUniformDecl>();

    /// <summary>Top-level non-<c>const</c> mutable globals (emitted as HLSL <c>static</c> globals).</summary>
    public IReadOnlyList<GlobalVarDecl> MutableGlobals { get; init; } = Array.Empty<GlobalVarDecl>();

    /// <summary>glslViewer-style alias name → ShaderToy built-in it was folded onto (e.g. u_time → iTime),
    /// applied to identifier references so a declared alias resolves to its built-in.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Names of IGNORED top-level <c>in</c>/<c>varying</c>/<c>attribute</c> declarations (web /
    /// desktop-export vertex-stage leftover) whose name is a conventional fullscreen screen-coordinate
    /// varying (see <see cref="ScreenCoordVaryings"/>). A reference to one of these resolves to the
    /// harness's normalized screen UV (<c>fragCoord / iResolution.xy</c>, [0,1]); a non-coordinate ignored
    /// varying is NOT here and stays an undeclared-identifier reject if referenced.
    /// </summary>
    public IReadOnlySet<string> ScreenUvAliases { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
}
