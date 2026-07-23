#nullable enable

using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Core;

/// <summary>
/// Wraps raw SM6 DXIL bytecode in the small header MonoGame 3.8.5's real DirectX12 writer
/// prepends before the per-shader <c>ShaderCode</c> field (see
/// <c>plan/PHASE-54-appendix/dx12-dxil-container-research.md</c> — read from MonoGame's own
/// source, not reverse-engineered). MonoGame's native DX12 <c>Shader_Create</c> looks for the
/// magic marker as the first 4 bytes and, if present, consumes the next two <see cref="int"/>s
/// as <c>maxSamplerSlot</c>/<c>maxTextureSlot</c> before treating the remainder as the raw
/// shader bytecode handed straight to <c>ID3D12Device::CreatePipelineState</c>.
/// </summary>
public static class DirectX12ShaderCodeWrapper
{
    private const uint MagicMarker = 0xB00B00;

    public static byte[] Wrap(
        ReadOnlySpan<byte> dxil,
        IReadOnlyList<TextureReflection> textures,
        IReadOnlyList<SamplerReflection> samplers)
    {
        int textureMaxSlot = -1;
        foreach (TextureReflection tex in textures)
        {
            if (tex.BindSlot > textureMaxSlot)
                textureMaxSlot = tex.BindSlot;
        }

        int samplerMaxSlot = -1;
        foreach (SamplerReflection samp in samplers)
        {
            if (samp.BindSlot > samplerMaxSlot)
                samplerMaxSlot = samp.BindSlot;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(MagicMarker);
        writer.Write(samplerMaxSlot);
        writer.Write(textureMaxSlot);
        writer.Write(dxil);
        writer.Flush();

        return stream.ToArray();
    }
}
