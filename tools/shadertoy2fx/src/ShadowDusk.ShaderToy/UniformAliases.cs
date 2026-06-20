namespace ShadowDusk.ShaderToy;

/// <summary>
/// The small set of common glslViewer / KodeLife uniform aliases that map exactly (same type) onto a
/// predefined ShaderToy built-in, so a shader that declares <c>uniform float u_time;</c> and uses
/// <c>u_time</c> Just Works without the consumer wiring a separate parameter.
///
/// <para>Only <b>exact-type</b> aliases are folded — mapping an alias whose type differs from the
/// built-in (e.g. glslViewer's <c>vec2 u_resolution</c> vs ShaderToy's <c>vec3 iResolution</c>, or
/// <c>vec2 u_mouse</c> vs <c>vec4 iMouse</c>) would silently change the shape a reference resolves to,
/// so those are deliberately NOT aliased: they are exposed verbatim as custom uniforms instead. This
/// keeps the alias nicety zero-risk.</para>
/// </summary>
internal static class UniformAliases
{
    // alias name -> (required GLSL type, ShaderToy built-in it maps to). Exact type match only.
    private static readonly IReadOnlyDictionary<string, (string Type, string Builtin)> Map =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            // glslViewer / KodeLife / Bonzomatic time aliases — all exact-type float onto iTime.
            ["u_time"] = ("float", "iTime"),
            ["iGlobalTime"] = ("float", "iTime"),
            ["time"] = ("float", "iTime"),
            ["fGlobalTime"] = ("float", "iTime"),   // Bonzomatic
            // Frame counter aliases — exact-type int onto iFrame.
            ["u_frame"] = ("int", "iFrame"),
            ["iGlobalFrame"] = ("int", "iFrame"),
        };

    /// <summary>
    /// If <paramref name="name"/> is a known alias AND its declared <paramref name="glslType"/> matches
    /// the alias's expected type, resolve it to the ShaderToy built-in. Otherwise returns false (the
    /// declaration should be treated as an ordinary custom uniform).
    /// </summary>
    public static bool TryResolve(string name, string glslType, out string? builtin)
    {
        builtin = null;
        if (Map.TryGetValue(name, out (string Type, string Builtin) entry) && entry.Type == glslType)
        {
            builtin = entry.Builtin;
            return true;
        }

        return false;
    }
}
