#nullable enable

using System.Text;
using ShadowDusk.Integration.Tests;

namespace ShadowDusk.ImageTests;

/// <summary>
/// A vertex+pixel GLSL pair extracted from a compiled mgfx blob. The vertex
/// source is nullable: PS-only effects (such as the SM 3.0 postprocess
/// shaders mgfxc compiles via MojoShader) have no compiled VS blob — the
/// renderer must inject a passthrough VS at draw time (see
/// <see cref="Rendering.PassthroughVertexShader"/>).
/// </summary>
public sealed record GlslShaderPair(string? VertexSource, string FragmentSource);

public static class GlslShaderExtractor
{
    /// <summary>
    /// Extracts the GLSL source pair for one (technique, pass) of a compiled
    /// mgfx blob. The blob must be in ShadowDusk's mgfx format (signature
    /// <c>XFGM</c>); mgfxc-format files (<c>MGFX</c>) are parsed by
    /// <see cref="MgfxcMgfxReader"/> instead.
    ///
    /// <para>
    /// If the pass's <c>VertexShaderIndex</c> is out of range
    /// (i.e., the pass is PS-only), the returned pair has
    /// <c>VertexSource = null</c>. The fragment source is always required;
    /// a missing PS index throws.
    /// </para>
    /// </summary>
    public static GlslShaderPair Extract(byte[] mgfx, int techniqueIndex = 0, int passIndex = 0)
    {
        if (mgfx is null || mgfx.Length == 0)
            throw new ArgumentException("mgfx blob is null or empty.", nameof(mgfx));

        var reader = MgfxBlobReader.Parse(mgfx);

        if (techniqueIndex < 0 || techniqueIndex >= reader.Techniques.Count)
            throw new ArgumentOutOfRangeException(
                nameof(techniqueIndex),
                techniqueIndex,
                $"Technique index out of range. Blob has {reader.Techniques.Count} technique(s).");

        var technique = reader.Techniques[techniqueIndex];

        if (passIndex < 0 || passIndex >= technique.Passes.Count)
            throw new ArgumentOutOfRangeException(
                nameof(passIndex),
                passIndex,
                $"Pass index out of range. Technique '{technique.Name}' has {technique.Passes.Count} pass(es).");

        var pass    = technique.Passes[passIndex];
        int vsIndex = pass.VertexShaderIndex;
        int psIndex = pass.PixelShaderIndex;

        if (psIndex < 0 || psIndex >= reader.ShaderBlobs.Count)
            throw new InvalidDataException(
                $"Pixel shader index {psIndex} is out of range for shader blob list of size {reader.ShaderBlobs.Count} (technique '{technique.Name}', pass '{pass.Name}').");

        string? vertexSource = null;
        if (vsIndex >= 0 && vsIndex < reader.ShaderBlobs.Count)
            vertexSource = Encoding.UTF8.GetString(reader.ShaderBlobs[vsIndex]);

        string fragmentSource = Encoding.UTF8.GetString(reader.ShaderBlobs[psIndex]);

        return new GlslShaderPair(vertexSource, fragmentSource);
    }
}
