namespace ShadowDusk.ShaderToy;

/// <summary>
/// The explicit GLSL → HLSL intrinsic mapping (trap 4). Every function call whose head is not a
/// user-defined function, not a type constructor, and not in one of these tables is a loud reject.
/// </summary>
internal static class IntrinsicTable
{
    /// <summary>GLSL intrinsics whose HLSL name differs (simple rename, same arg order/semantics).</summary>
    public static readonly IReadOnlyDictionary<string, string> Renames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mix"] = "lerp",
            ["fract"] = "frac",
            ["inversesqrt"] = "rsqrt",
            ["dFdx"] = "ddx",
            ["dFdy"] = "ddy",
            ["texture"] = "tex2D",
            ["texture2D"] = "tex2D",
            ["textureLod"] = "tex2Dlod",
            ["textureGrad"] = "tex2Dgrad",
        };

    /// <summary>Intrinsics whose name is identical in HLSL and whose semantics carry over directly.</summary>
    public static readonly IReadOnlySet<string> SameName = new HashSet<string>(StringComparer.Ordinal)
    {
        "clamp", "min", "max", "abs", "floor", "ceil", "round", "trunc", "sign", "sqrt",
        "exp", "log", "exp2", "log2", "pow", "sin", "cos", "tan", "asin", "acos",
        "sinh", "cosh", "tanh", "step", "smoothstep", "length", "distance", "dot",
        "cross", "normalize", "reflect", "refract", "radians", "degrees", "saturate",

        // fwidth(x) = abs(ddx(x)) + abs(ddy(x)) — a same-named HLSL intrinsic available in ps_2_x+
        // (the harness targets ps_3_0), so it maps faithfully.
        "fwidth",
    };

    /// <summary>
    /// Intrinsics handled by a dedicated rewrite (not a simple rename) in the emitter:
    /// <c>atan</c> (1-arg → atan, 2-arg → atan2) and <c>mod</c> (sign-correct helper, trap 3).
    /// </summary>
    public static readonly IReadOnlySet<string> Special = new HashSet<string>(StringComparer.Ordinal)
    {
        "atan", "mod", "matrixCompMult",
    };

    /// <summary>Intrinsics that are explicitly rejected with a tailored message.</summary>
    public static readonly IReadOnlyDictionary<string, string> Rejected =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["texelFetch"] = "'texelFetch' is outside the supported subset (no integer texel addressing in fx_2_0).",
            ["textureProj"] = "'textureProj' is outside the supported subset.",
            ["textureSize"] = "'textureSize' is outside the supported subset (use iChannelResolution).",
            ["dFdxFine"] = "fine/coarse derivatives are outside the supported subset.",
            ["dFdyFine"] = "fine/coarse derivatives are outside the supported subset.",
            ["dFdxCoarse"] = "fine/coarse derivatives are outside the supported subset.",
            ["dFdyCoarse"] = "fine/coarse derivatives are outside the supported subset.",
            ["bitfieldExtract"] = "integer bitfield intrinsics are outside the supported subset.",
            ["packHalf2x16"] = "bit-packing intrinsics are outside the supported subset.",

            // GLSL roundEven is banker's (round-half-to-even) rounding; HLSL `round` and a
            // floor(x+0.5) form are both round-half-up, so there is no faithful HLSL map. Reject
            // loudly rather than emit a subtly-different rounding.
            ["roundEven"] =
                "'roundEven' (round-half-to-even) has no faithful HLSL equivalent and is outside the supported subset.",
        };

    /// <summary>True if <paramref name="name"/> is any recognized intrinsic (mapped or special).</summary>
    public static bool IsKnownIntrinsic(string name) =>
        Renames.ContainsKey(name) || SameName.Contains(name) || Special.Contains(name);
}
