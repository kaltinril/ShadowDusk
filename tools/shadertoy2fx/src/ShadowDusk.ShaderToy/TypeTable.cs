namespace ShadowDusk.ShaderToy;

/// <summary>
/// The fixed table of supported GLSL type spellings and their HLSL equivalents, plus helpers to
/// classify a type name (vector/matrix/scalar). Anything not present here is outside the subset.
/// </summary>
internal static class TypeTable
{
    /// <summary>GLSL type spelling → HLSL type spelling (the trap-1 mapping).</summary>
    public static readonly IReadOnlyDictionary<string, string> GlslToHlsl =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["void"] = "void",
            ["bool"] = "bool",
            ["int"] = "int",
            ["float"] = "float",
            ["vec2"] = "float2",
            ["vec3"] = "float3",
            ["vec4"] = "float4",
            ["ivec2"] = "int2",
            ["ivec3"] = "int3",
            ["ivec4"] = "int4",
            ["bvec2"] = "bool2",
            ["bvec3"] = "bool3",
            ["bvec4"] = "bool4",
            ["mat2"] = "float2x2",
            ["mat3"] = "float3x3",
            ["mat4"] = "float4x4",
        };

    /// <summary>True if <paramref name="name"/> is a supported GLSL type spelling.</summary>
    public static bool IsTypeName(string name) => GlslToHlsl.ContainsKey(name);

    /// <summary>Translate a GLSL type spelling to HLSL. Caller must have validated it.</summary>
    public static string ToHlsl(string glslType) => GlslToHlsl[glslType];

    /// <summary>Resolve a GLSL type spelling into a <see cref="GlslType"/> descriptor.</summary>
    public static GlslType Resolve(string name) => name switch
    {
        "void" => GlslType.ScalarOf(ScalarKind.Void),
        "bool" => GlslType.ScalarOf(ScalarKind.Bool),
        "int" => GlslType.ScalarOf(ScalarKind.Int),
        "float" => GlslType.ScalarOf(ScalarKind.Float),
        "vec2" => GlslType.Vector(ScalarKind.Float, 2),
        "vec3" => GlslType.Vector(ScalarKind.Float, 3),
        "vec4" => GlslType.Vector(ScalarKind.Float, 4),
        "ivec2" => GlslType.Vector(ScalarKind.Int, 2),
        "ivec3" => GlslType.Vector(ScalarKind.Int, 3),
        "ivec4" => GlslType.Vector(ScalarKind.Int, 4),
        "bvec2" => GlslType.Vector(ScalarKind.Bool, 2),
        "bvec3" => GlslType.Vector(ScalarKind.Bool, 3),
        "bvec4" => GlslType.Vector(ScalarKind.Bool, 4),
        "mat2" => GlslType.Matrix(2),
        "mat3" => GlslType.Matrix(3),
        "mat4" => GlslType.Matrix(4),
        _ => GlslType.Unknown,
    };

    /// <summary>Type spellings explicitly rejected with a tailored message (better than "unknown").</summary>
    public static readonly IReadOnlyDictionary<string, string> RejectedTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["double"] = "'double' is outside the supported subset (use 'float').",
            ["dvec2"] = "double-precision vectors are outside the supported subset.",
            ["dvec3"] = "double-precision vectors are outside the supported subset.",
            ["dvec4"] = "double-precision vectors are outside the supported subset.",
            ["uint"] = "'uint' is outside the supported subset (use 'int').",
            ["uvec2"] = "unsigned vectors are outside the supported subset.",
            ["uvec3"] = "unsigned vectors are outside the supported subset.",
            ["uvec4"] = "unsigned vectors are outside the supported subset.",
            ["mat2x2"] = "non-square / explicit mat?x? spellings are outside the supported subset (use mat2/3/4).",
            ["mat3x3"] = "non-square / explicit mat?x? spellings are outside the supported subset (use mat2/3/4).",
            ["mat4x4"] = "non-square / explicit mat?x? spellings are outside the supported subset (use mat2/3/4).",
            ["mat2x3"] = "non-square matrices are outside the supported subset.",
            ["mat3x2"] = "non-square matrices are outside the supported subset.",
            ["sampler3D"] = "3D samplers are outside the supported subset (iChannelN is 2D only).",
            ["samplerCube"] = "cube samplers are outside the supported subset (iChannelN is 2D only).",
        };
}
