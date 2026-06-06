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
///
/// <para>
/// <paramref name="ParameterRegisters"/> maps each effect parameter NAME to its
/// MojoShader constant-register index (the <c>{vs|ps}_uniforms_vec4[reg]</c>
/// slot the rewritten GLSL reads). It is computed from the .mgfx constant-buffer
/// offset table (<c>register = byteOffset / 16</c>) and lets the renderer upload
/// a named scene uniform into the right array slot, EXACTLY as MonoGame's real
/// GL runtime does (<c>glUniform4fv("{vs|ps}_uniforms_vec4", …)</c> keyed on the
/// reflected cbuffer layout). This is the faithful contract a VS-driven .mgfx
/// carries — see Phase 28 rung-4 validation.
/// </para>
/// </summary>
public sealed record GlslShaderPair(
    string? VertexSource,
    string FragmentSource,
    IReadOnlyDictionary<string, int> ParameterRegisters)
{
    /// <summary>Back-compat: no register map (PS-only callers that don't need it).</summary>
    public GlslShaderPair(string? vertexSource, string fragmentSource)
        : this(vertexSource, fragmentSource, EmptyRegisters)
    {
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyRegisters =
        new Dictionary<string, int>();
}

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

        // Build the parameter NAME -> MojoShader constant-register map from the .mgfx
        // cbuffer offset table. MonoGame's GL runtime stores each cbuffer variable at a
        // 16-byte register and uploads the whole cbuffer via glUniform4fv into the
        // {vs|ps}_uniforms_vec4[] array; register = byteOffset / 16. (A mat4 spans four
        // consecutive registers starting at its offset; the renderer uploads its four
        // columns from that base register.) This is the same offset table the runtime
        // reflects — so honoring it makes the proxy faithful, not invented.
        var registers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, byteOffset) in reader.ParameterOffsets)
            registers[name] = byteOffset / 16;

        return new GlslShaderPair(vertexSource, fragmentSource, registers);
    }
}
