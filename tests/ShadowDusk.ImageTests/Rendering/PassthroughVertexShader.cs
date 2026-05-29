#nullable enable

namespace ShadowDusk.ImageTests.Rendering;

/// <summary>
/// Canned passthrough vertex shader sources used to pair with a PS-only mgfx
/// pixel shader at render time. mgfxc commonly produces .mgfx files containing
/// only a pixel-shader blob — there is no compiled VS to use, but rendering
/// requires both stages. We inject a passthrough VS that forwards the standard
/// quad attributes to whichever varying names the supplied PS expects.
///
/// <para>
/// Two dialects are produced so the same renderer can run output from either
/// shader compiler:
/// </para>
/// <list type="bullet">
///   <item><see cref="MojoShaderDialect"/> — GLSL ES 1.0 style (mgfxc /
///         MojoShader): <c>attribute</c>, <c>varying</c>, <c>vs_v0</c>,
///         <c>vFrontColor</c>, <c>vTexCoord0</c>.</item>
///   <item><see cref="SpirvCrossDialect"/> — GLSL 3.30 (ShadowDusk /
///         SPIRV-Cross): <c>in</c>, <c>out</c>, <c>in_var_POSITION</c>,
///         <c>var_COLOR0</c>, <c>var_TEXCOORD0</c>. Matches the rewriting that
///         <see cref="ShaderSceneRenderer"/> performs on a PS to make varyings
///         link.</item>
/// </list>
/// <para>
/// <see cref="PickFor"/> sniffs a PS source to decide which dialect to use:
/// any source containing <c>varying</c> or <c>gl_FragColor</c> is treated as
/// the legacy MojoShader dialect; everything else is modern.
/// </para>
/// </summary>
public static class PassthroughVertexShader
{
    /// <summary>
    /// GLSL ES 1.0 style passthrough VS that matches MojoShader's expected
    /// attribute and varying names.
    ///
    /// <para>
    /// MojoShader names attributes <c>vs_v0..vs_vN</c>, indexed in the order
    /// HLSL semantics appear, and names PS-input varyings <c>vFrontColor</c>
    /// (interpolated COLOR0) and <c>vTexCoord0..vTexCoordN</c>
    /// (interpolated TEXCOORDn). For our standard quad we forward
    /// POSITION (<c>vs_v0</c>), COLOR0 (<c>vs_v1</c>), TEXCOORD0
    /// (<c>vs_v2</c>) into <c>gl_Position</c>, <c>vFrontColor</c>,
    /// <c>vTexCoord0</c>.
    /// </para>
    /// <para>
    /// No <c>#version</c> directive — this targets the legacy unversioned
    /// GLSL dialect that requires a compatibility-profile GL context.
    /// </para>
    /// </summary>
    public const string MojoShaderDialect =
        "attribute vec4 vs_v0;\n" +
        "attribute vec4 vs_v1;\n" +
        "attribute vec2 vs_v2;\n" +
        "varying vec4 vFrontColor;\n" +
        "varying vec4 vTexCoord0;\n" +
        "void main()\n" +
        "{\n" +
        "    gl_Position = vs_v0;\n" +
        "    vFrontColor = vs_v1;\n" +
        "    vTexCoord0  = vec4(vs_v2, 0.0, 0.0);\n" +
        "}\n";

    /// <summary>
    /// Modern GLSL 3.30 passthrough VS matching SPIRV-Cross naming
    /// (<c>in_var_&lt;SEM&gt;</c> inputs, <c>var_&lt;SEM&gt;</c> outputs after
    /// ShaderSceneRenderer's varying rewrite). Used when the PS is in the
    /// modern dialect (e.g., a ShadowDusk-compiled mgfx that happens to be
    /// PS-only).
    /// </summary>
    public const string SpirvCrossDialect =
        "#version 330\n" +
        "in vec4 in_var_POSITION;\n" +
        "in vec4 in_var_COLOR0;\n" +
        "in vec2 in_var_TEXCOORD0;\n" +
        "out vec4 var_COLOR0;\n" +
        "out vec2 var_TEXCOORD0;\n" +
        "void main()\n" +
        "{\n" +
        "    gl_Position    = in_var_POSITION;\n" +
        "    var_COLOR0     = in_var_COLOR0;\n" +
        "    var_TEXCOORD0  = in_var_TEXCOORD0;\n" +
        "}\n";

    /// <summary>
    /// Picks the passthrough VS dialect that matches the supplied pixel
    /// shader source. Returns <see cref="MojoShaderDialect"/> if the PS
    /// looks like legacy GLSL ES (contains <c>varying</c> or
    /// <c>gl_FragColor</c>); otherwise <see cref="SpirvCrossDialect"/>.
    /// </summary>
    public static string PickFor(string pixelSource)
    {
        ArgumentNullException.ThrowIfNull(pixelSource);
        if (pixelSource.Contains("varying ", StringComparison.Ordinal) ||
            pixelSource.Contains("gl_FragColor", StringComparison.Ordinal))
        {
            return MojoShaderDialect;
        }
        return SpirvCrossDialect;
    }
}
