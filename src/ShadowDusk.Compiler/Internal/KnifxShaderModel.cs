#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The per-shader <c>ShaderVersion</c> a KNIFX container records, read from a pass's resolved
/// <c>compile</c> profile. MGFX v10 ignores the value; KNI's DX11 and GL runtimes refuse a shader
/// whose version exceeds the graphics profile's ceiling (Reach 2.0, HiDef 3.0, FL10_0 4.0,
/// FL10_1 4.1, FL11_x 5.0), so the value decides whether an effect loads.
/// </summary>
/// <remarks>
/// The Direct3D feature-level-9 forms follow KNI's own effect processor
/// (<c>Microsoft.Xna.Framework.Content.Pipeline.EffectCompiler.ShaderVersion.ParseShaderModel</c>,
/// the same code in KNI 4.2.9001 and 4.3.9001): <c>*_4_0_level_9_1</c> is 2.0 and
/// <c>*_4_0_level_9_3</c> is 3.0, not the 4.0 a plain <c>_major_minor</c> read gives. Measured by
/// running KNI's own MGCB on both, literal and macro-produced: <c>/platform:Windows</c> on 4.2.9001
/// and 4.3.9001, <c>/platform:DesktopGL</c> on 4.3.9001 (4.2.9001's GL arm rests on the identical
/// decompile). Recording 4.0 made every MonoGame-template effect
/// (<c>#define PS_SHADERMODEL ps_4_0_level_9_1</c>) unloadable on HiDef.
///
/// <para>Every other profile keeps the reading it always had, even where KNI's own processor differs
/// (it refuses <c>*_4_0_level_9_0</c>, <c>*_2_a</c>, <c>*_sw</c> and SM1 as invalid profiles):
/// those outputs already load somewhere a consumer may rely on (a desktop GL body at 4.0 loads on
/// FL10_0 and up; the GLES/WebGL body records 0.0 and skips the check), so changing them is not this
/// reader's call. See the known-gap rows in <c>project_facts.md</c>. An SM6 profile never reaches
/// this reader on a KNIFX container: it is refused with <c>SD0015</c> on DirectX and, since issue
/// #439, on OpenGL too (a 6.0 body loaded on no KNI graphics profile).</para>
/// </remarks>
internal static class KnifxShaderModel
{
    private static readonly Regex MajorMinor = new(@"_(\d)_(\d)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The (Major, Minor) recorded for <paramref name="profile"/>. Defaults to (3,0), the
    /// MojoShader GL ceiling, when the profile is absent or unparseable.
    /// </summary>
    internal static (int Major, int Minor) Parse(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
            return (3, 0);

        if (profile.EndsWith("_4_0_level_9_1", StringComparison.OrdinalIgnoreCase))
            return (2, 0);
        if (profile.EndsWith("_4_0_level_9_3", StringComparison.OrdinalIgnoreCase))
            return (3, 0);

        Match m = MajorMinor.Match(profile);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int major)
            && int.TryParse(m.Groups[2].Value, out int minor))
            return (major, minor);

        return (3, 0);
    }
}
