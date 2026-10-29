#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The per-shader <c>ShaderVersion</c> a KNIFX container records, read from a pass's resolved
/// <c>compile</c> profile the way KNI's own effect processor reads it
/// (<c>Microsoft.Xna.Framework.Content.Pipeline.EffectCompiler.ShaderVersion.ParseShaderModel</c>,
/// identical in KNI 4.2.9001 and 4.3.9001). MGFX v10 ignores the value; KNI's DX11 and GL
/// runtimes refuse a shader whose version exceeds the graphics profile's ceiling (Reach 2.0,
/// HiDef 3.0, FL10_0 4.0, FL10_1 4.1, FL11_x 5.0), so the value decides whether an effect loads.
/// </summary>
/// <remarks>
/// The Direct3D feature-level-9 forms are the case a plain <c>_major_minor</c> read gets wrong:
/// KNI records <c>*_4_0_level_9_1</c> as 2.0 and <c>*_4_0_level_9_3</c> as 3.0, not 4.0 (measured
/// by running KNI's own MGCB on both, literal and macro-produced, for <c>/platform:Windows</c> and
/// <c>/platform:DesktopGL</c>). Recording 4.0 made every MonoGame-template effect
/// (<c>#define PS_SHADERMODEL ps_4_0_level_9_1</c>) unloadable on HiDef. <c>*_4_0_level_9_0</c> is
/// not in KNI's grammar at all (its pipeline reports <c>Invalid profile</c>), so it is refused by
/// <see cref="ProfileCheck"/> rather than given a version KNI would never write.
/// </remarks>
internal static class KnifxShaderModel
{
    private static readonly Regex MajorMinor = new(@"_(\d)_(\d)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The (Major, Minor) KNI records for <paramref name="profile"/>. Defaults to (3,0), the
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

    /// <summary>
    /// <c>SD0018</c> when a KNIFX compile's resolved profile is one KNI's effect processor
    /// refuses as an invalid profile (<c>*_4_0_level_9_0</c>), else <c>null</c>. On DirectX the
    /// same profile is already <c>SD0015</c> (below mgfxc's DirectX_11 floor), so this is the
    /// OpenGL KNIFX case in practice.
    /// </summary>
    internal static ShaderError? ProfileCheck(
        string? profile, ShaderStage stage, SourceSpan? span, string sourceFileName, string? entryPoint)
    {
        if (string.IsNullOrEmpty(profile)
            || !profile.EndsWith("_4_0_level_9_0", StringComparison.OrdinalIgnoreCase))
            return null;

        string stageWord = stage == ShaderStage.Vertex ? "Vertex" : "Pixel";
        string prefix    = stage == ShaderStage.Vertex ? "vs" : "ps";
        string named     = entryPoint is null ? string.Empty : $" '{entryPoint}'";
        return new ShaderError(
            File: sourceFileName,
            Line: span?.StartLine ?? 0,
            Column: span?.StartColumn ?? 0,
            Code: "SD0018",
            Message: $"compile target '{profile}' is not a profile KNI accepts for the KNIFX container; KNI's own " +
                     $"effect processor rejects it with \"Invalid profile '{profile}'. {stageWord} shader{named}.\" " +
                     $"(KNI reads only the _level_9_1 and _level_9_3 feature-level forms). Use {prefix}_4_0_level_9_1, " +
                     $"which KNI records as shader model 2.0, or the {prefix}_3_0 the standard '#if OPENGL' arm names");
    }
}
