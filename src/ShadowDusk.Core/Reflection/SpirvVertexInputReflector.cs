#nullable enable

using ShadowDusk.Core.Reflection.Spirv;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Builds the <c>.mgfx</c> per-shader vertex ATTRIBUTE TABLE from a vertex shader's SPIR-V,
/// mirroring real mgfxc's Vulkan writer (<c>ShaderProfile.Vulkan.CreateShader</c>): the inputs
/// are ordered by <c>Location</c>, each semantic maps to a MonoGame <c>VertexElementUsage</c>
/// byte plus its semantic index, and an input that spans several locations (a matrix, an array)
/// contributes one entry per location with a running index.
///
/// <para><b>This table IS load-bearing on the new native backend (correction, Phase 54 follow-up,
/// 2026-07-23).</b> An earlier version of this remark claimed MonoGame's native backends build
/// their vertex input layout positionally and never read this table — that is false. The shared
/// managed <c>VertexInputLayout.GenerateInputElements</c> (used by every native backend,
/// including DirectX12) iterates this exact table to match declared vertex-buffer elements
/// against the shader's required inputs; an empty table silently yields a zero-element input
/// layout (its "missing input" check only runs inside the per-attribute loop, so it never fires
/// when the table itself is empty), which then fails DirectX12's
/// <c>CreateGraphicsPipelineState</c> — called lazily right before the first Draw — with
/// <c>E_INVALIDARG</c>. Confirmed by reading MonoGame's real v3.8.5 source directly
/// (<c>VertexInputLayout.Native.cs</c>, <c>Shader.Native.cs</c>). Always populate this table for
/// a native-backend vertex shader; never assume it is decorative.</para>
///
/// <para>The <c>Name</c> and <c>Location</c> fields are written as <c>""</c> / <c>0</c> —
/// exactly what mgfxc emits for a Vulkan shader (its GL profile is the one that populates
/// them); <c>Usage</c>/<c>Index</c> are what <c>GenerateInputElements</c> actually matches on.</para>
/// </summary>
public static class SpirvVertexInputReflector
{
    /// <summary>
    /// Reads the vertex attribute table from <paramref name="spirvBlob"/>. Returns an empty
    /// list if the blob is not parseable SPIR-V or declares no located inputs — this is a
    /// last-resort fallback, not a safe default (see class remarks); a real reflection failure
    /// here will surface as a load/draw failure downstream, not silently.
    /// </summary>
    public static IReadOnlyList<MgfxVertexAttributeInfo> Read(ReadOnlyMemory<byte> spirvBlob)
    {
        SpirvModule? module = SpirvModule.TryParse(spirvBlob.Span);
        if (module is null)
            return Array.Empty<MgfxVertexAttributeInfo>();

        IReadOnlyList<(string Semantic, int Location, int LocationCount)> inputs;
        try
        {
            inputs = new SpirvReflectionParser(module).ReflectVertexInputs();
        }
        catch (Exception)
        {
            return Array.Empty<MgfxVertexAttributeInfo>();
        }

        var attributes = new List<MgfxVertexAttributeInfo>(inputs.Count);

        foreach ((string semantic, _, int locationCount) in inputs)
        {
            (byte usage, int baseIndex) = VertexSemanticMapper.Map(semantic);

            for (int i = 0; i < locationCount; i++)
            {
                attributes.Add(new MgfxVertexAttributeInfo(
                    Name:     string.Empty,
                    Usage:    usage,
                    Index:    (byte)(baseIndex + i),
                    Location: 0));
            }
        }

        return attributes;
    }
}
