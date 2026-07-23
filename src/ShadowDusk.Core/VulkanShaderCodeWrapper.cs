#nullable enable

using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Core;

/// <summary>
/// Wraps raw SPIR-V bytecode in the Vulkan descriptor-layout header MonoGame 3.8.5's
/// real Vulkan writer prepends before the per-shader <c>ShaderCode</c> field (see
/// <c>plan/DONE/PHASE-32-appendix/vulkan-mgfx-format-spec.md</c> — read from MonoGame's own
/// source, not reverse-engineered). MonoGame's DesktopVK <c>Effect</c> reader expects
/// this exact shape for the Vulkan profile; every other profile writes raw bytecode
/// unwrapped.
/// </summary>
public static class VulkanShaderCodeWrapper
{
    private const uint UniformBufferDynamic  = 8;
    private const uint CombinedImageSampler  = 1;
    private const uint SampledImage          = 2;
    private const uint SamplerOnly           = 0;

    private const uint StageFlagsVertex = 0x00000001;
    private const uint StageFlagsPixel  = 0x00000010;

    public static byte[] Wrap(
        ReadOnlySpan<byte> spirv,
        ShaderStage stage,
        ConstantBufferReflection? constantBuffer,
        IReadOnlyList<TextureReflection> textures,
        IReadOnlyList<SamplerReflection> samplers)
    {
        uint uniformSlots = 0, textureSlots = 0, samplerSlots = 0;
        var textureTypes = new uint[16];
        var bindings = new List<(uint Binding, uint DescriptorType, uint StageFlags)>();
        uint stageFlags = stage == ShaderStage.Vertex ? StageFlagsVertex : StageFlagsPixel;

        if (constantBuffer is not null)
        {
            // The cbuffer's real SPIR-V binding (NOT necessarily 0 — it must be reflected
            // like any other resource, or it can collide with an auto-assigned texture
            // binding that happens to land on 0 too).
            uniformSlots |= 1u << 0;
            bindings.Add(((uint)constantBuffer.RawBinding, UniformBufferDynamic, stageFlags));
        }

        foreach (TextureReflection tex in textures)
        {
            if (tex.BindSlot is >= 0 and < 16)
                textureTypes[tex.BindSlot] = ToTextureType(tex.Dimension);
            if (tex.BindSlot is >= 0 and < 32)
                textureSlots |= 1u << tex.BindSlot;

            // "Combined" is decided by raw-binding equality, not the SPIR-V type
            // (OpTypeSampledImage vs separate OpTypeImage+OpTypeSampler): Vulkan
            // allows a single COMBINED_IMAGE_SAMPLER descriptor to satisfy two
            // separate SPIR-V resource variables that share one binding, and two
            // descriptor-set-layout bindings at the SAME binding number is invalid
            // regardless of the underlying SPIR-V shape — confirmed by a minimal
            // repro against a real DesktopVK runtime (2026-07-18).
            SamplerReflection? paired = samplers.FirstOrDefault(s => s.RawBinding == tex.RawBinding);

            // A combined descriptor occupies BOTH the texture and the sampler slot masks —
            // mgfxc sets both (`textureSlots |= 1 << slot; samplerSlots |= 1 << slot;`) for the
            // same-slot case. The masks drive MGVK_UpdateDescriptors' dirty early-out
            // (`textureSamplerDirty & samplerSlots`), so leaving samplerSlots clear made a
            // sampler-only state change look non-dirty (issue #145 audit, divergence S2).
            if (paired is not null && tex.BindSlot is >= 0 and < 32)
                samplerSlots |= 1u << tex.BindSlot;

            bindings.Add(((uint)tex.RawBinding, paired is not null ? CombinedImageSampler : SampledImage, stageFlags));
        }

        foreach (SamplerReflection samp in samplers)
        {
            if (textures.Any(t => t.RawBinding == samp.RawBinding))
                continue; // already emitted alongside its texture above

            if (samp.BindSlot is >= 0 and < 32)
                samplerSlots |= 1u << samp.BindSlot;

            bindings.Add(((uint)samp.RawBinding, SamplerOnly, stageFlags));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(constantBuffer is not null ? 1 : 0);
        writer.Write(uniformSlots);
        writer.Write(textureSlots);
        writer.Write(samplerSlots);
        foreach (uint t in textureTypes)
            writer.Write(t);

        writer.Write((uint)bindings.Count);
        foreach (var (binding, descriptorType, flags) in bindings)
        {
            writer.Write(binding);
            writer.Write(descriptorType);
            writer.Write((uint)1); // descriptorCount — always 1
            writer.Write(flags);
            writer.Write((ulong)0); // pImmutableSamplers — always null
        }

        writer.Write(spirv);
        writer.Flush();
        return stream.ToArray();
    }

    private static uint ToTextureType(TextureDimension dim) => dim switch
    {
        TextureDimension.Texture3D  => 1,
        TextureDimension.TextureCube => 2,
        _ => 0, // 2D (and 1D/Unknown default to 2D, matching mgfxc's own default case)
    };
}
