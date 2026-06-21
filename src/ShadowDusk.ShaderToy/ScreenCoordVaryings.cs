namespace ShadowDusk.ShaderToy;

/// <summary>
/// The conventional names a web / desktop-export vertex stage uses for the interpolated fullscreen
/// screen-coordinate varying ([0,1] across the quad). A top-level <c>in</c>/<c>varying</c>/<c>attribute</c>
/// declaration is vertex-stage leftover the converter IGNORES (the harness synthesizes its own vertex
/// shader); but the COMMON case is that such a varying is then referenced in the fragment body as the
/// fullscreen UV. For this well-known set of names we faithfully alias the varying to the harness's
/// normalized screen UV (<c>fragCoord / iResolution.xy</c>, [0,1], ShaderToy bottom-left origin) instead
/// of leaving the reference dangling.
///
/// <para><b>Heuristic boundary (documented in MAPPING.md).</b> This only fires for these conventional
/// coordinate-varying spellings. An ignored varying with a NON-coordinate / unknown name that is actually
/// referenced stays a loud, located "undeclared identifier" reject — we cannot invent its per-vertex
/// value.</para>
/// </summary>
internal static class ScreenCoordVaryings
{
    /// <summary>The conventional fullscreen screen-coordinate varying names that alias the harness UV.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "texCoord",
        "vUv",
        "vUV",
        "v_texcoord",
        "vTextureCoord",
        "vTexCoord",
        "v_coord",
        "uv",
        "texcoord",
    };

    /// <summary>True if <paramref name="name"/> is a conventional screen-coordinate varying name.</summary>
    public static bool IsScreenCoordName(string name) => Names.Contains(name);
}
