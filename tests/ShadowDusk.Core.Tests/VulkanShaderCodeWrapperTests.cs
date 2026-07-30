#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Round-trip byte-shape tests for <see cref="VulkanShaderCodeWrapper"/> against the
/// format read directly from MonoGame 3.8.5's own source
/// (<c>plan/DONE/PHASE-32-appendix/vulkan-mgfx-format-spec.md</c>): a descriptor-layout
/// header prepended to the raw SPIR-V, consumed by the real DesktopVK <c>Effect</c>
/// reader.
/// </summary>
public sealed class VulkanShaderCodeWrapperTests
{
    private static readonly byte[] Spirv = { 0x03, 0x02, 0x23, 0x07, 0xAA, 0xBB }; // fake magic + payload

    private sealed class R
    {
        private readonly BinaryReader _br;
        public R(byte[] data) => _br = new BinaryReader(new MemoryStream(data));
        public int Int32() => _br.ReadInt32();
        public uint UInt32() => _br.ReadUInt32();
        public ulong UInt64() => _br.ReadUInt64();
        public byte[] Rest() => _br.ReadBytes((int)(_br.BaseStream.Length - _br.BaseStream.Position));
    }

    [Fact]
    public void Wrap_NoResources_HeaderAllZeroAndSpirvAppended()
    {
        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: null,
            textures: Array.Empty<TextureReflection>(), samplers: Array.Empty<SamplerReflection>());

        var r = new R(wrapped);
        r.Int32().ShouldBe(0, customMessage: "no constant buffer");
        r.UInt32().ShouldBe(0u, customMessage: "uniformSlots");
        r.UInt32().ShouldBe(0u, customMessage: "textureSlots");
        r.UInt32().ShouldBe(0u, customMessage: "samplerSlots");
        for (int i = 0; i < 16; i++)
            r.UInt32().ShouldBe(0u, customMessage: $"textureTypes[{i}]");
        r.UInt32().ShouldBe(0u, customMessage: "bindingCount");
        r.Rest().ShouldBe(Spirv);
    }

    [Fact]
    public void Wrap_ConstantBuffer_UsesRealRawBindingNotHardcodedZero()
    {
        // The real container's cbuffer binding is NOT reliably 0 (a real collision was
        // found empirically against ShadowDusk's own DXC output) — it must come from
        // the actual reflected SPIR-V decoration.
        var cbuffer = new ConstantBufferReflection
        {
            Name = "$Globals", SizeBytes = 16, BindSlot = 0,
            Variables = Array.Empty<VariableReflection>(), RawBinding = 3,
        };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: cbuffer,
            textures: Array.Empty<TextureReflection>(), samplers: Array.Empty<SamplerReflection>());

        var r = new R(wrapped);
        r.Int32().ShouldBe(1, customMessage: "one constant buffer");
        r.UInt32().ShouldBe(1u, customMessage: "uniformSlots bit 0 set");
        r.UInt32(); r.UInt32(); // textureSlots, samplerSlots
        for (int i = 0; i < 16; i++) r.UInt32(); // textureTypes
        r.UInt32().ShouldBe(1u, customMessage: "bindingCount");
        r.UInt32().ShouldBe(3u, customMessage: "binding == the cbuffer's real RawBinding, not 0");
        r.UInt32().ShouldBe(8u, customMessage: "descriptorType == UNIFORM_BUFFER_DYNAMIC");
        r.UInt32().ShouldBe(1u, customMessage: "descriptorCount");
        r.UInt32().ShouldBe(0x10u, customMessage: "stageFlags == FRAGMENT_BIT for Pixel stage");
        r.UInt64().ShouldBe(0ul, customMessage: "pImmutableSamplers");
    }

    [Fact]
    public void Wrap_VertexStage_UsesVertexStageFlag()
    {
        var cbuffer = new ConstantBufferReflection
        {
            Name = "$Globals", SizeBytes = 16, BindSlot = 0,
            Variables = Array.Empty<VariableReflection>(), RawBinding = 0,
        };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Vertex, constantBuffer: cbuffer,
            textures: Array.Empty<TextureReflection>(), samplers: Array.Empty<SamplerReflection>());

        var r = new R(wrapped);
        r.Int32(); r.UInt32(); r.UInt32(); r.UInt32();
        for (int i = 0; i < 16; i++) r.UInt32();
        r.UInt32(); // bindingCount
        r.UInt32(); // binding
        r.UInt32(); // descriptorType
        r.UInt32(); // descriptorCount
        r.UInt32().ShouldBe(0x01u, customMessage: "stageFlags == VERTEX_BIT for Vertex stage");
    }

    [Fact]
    public void Wrap_SeparateTextureAndSampler_EmitsTwoBindingsSampledImageAndSampler()
    {
        var texture = new TextureReflection
        {
            Name = "s0Texture", BindSlot = 0, Dimension = TextureDimension.Texture2D, RawBinding = 5,
        };
        var sampler = new SamplerReflection
        {
            Name = "s0", BindSlot = 0, RawBinding = 7, IsCombined = false,
        };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: null,
            textures: new[] { texture }, samplers: new[] { sampler });

        var r = new R(wrapped);
        r.Int32(); r.UInt32(); r.UInt32(); r.UInt32();
        for (int i = 0; i < 16; i++) r.UInt32();
        r.UInt32().ShouldBe(2u, customMessage: "one SAMPLED_IMAGE + one SAMPLER binding");

        r.UInt32().ShouldBe(5u, customMessage: "texture's own RawBinding");
        r.UInt32().ShouldBe(2u, customMessage: "descriptorType == SAMPLED_IMAGE (separate, not combined)");
        r.UInt32(); r.UInt32(); r.UInt64();

        r.UInt32().ShouldBe(7u, customMessage: "sampler's own RawBinding");
        r.UInt32().ShouldBe(0u, customMessage: "descriptorType == SAMPLER");
    }

    [Fact]
    public void Wrap_CombinedSampler_EmitsOneCombinedImageSamplerBinding()
    {
        var texture = new TextureReflection
        {
            Name = "s0", BindSlot = 0, Dimension = TextureDimension.Texture2D, RawBinding = 4,
        };
        var sampler = new SamplerReflection
        {
            Name = "s0", BindSlot = 0, RawBinding = 4, IsCombined = true,
        };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: null,
            textures: new[] { texture }, samplers: new[] { sampler });

        var r = new R(wrapped);
        r.Int32(); r.UInt32(); r.UInt32(); r.UInt32();
        for (int i = 0; i < 16; i++) r.UInt32();
        r.UInt32().ShouldBe(1u, customMessage: "combined resource is ONE binding, not two");
        r.UInt32().ShouldBe(4u, customMessage: "shared RawBinding");
        r.UInt32().ShouldBe(1u, customMessage: "descriptorType == COMBINED_IMAGE_SAMPLER");
    }

    [Fact]
    public void Wrap_SeparateResourcesSharingARawBinding_MustCombineNotDuplicate()
    {
        // The real, empirically-confirmed bug (2026-07-18): when a texture and its
        // sampler are declared with EXPLICIT matching HLSL registers (e.g. both
        // `register(t0)`/`register(s0)`), DXC's -fvk-t-shift/-fvk-s-shift lands them
        // at the SAME raw SPIR-V binding even though they remain two separate SPIR-V
        // variables (not one OpTypeSampledImage) — so IsCombined is false here. Two
        // Vulkan descriptor-set-layout bindings at the SAME binding number is invalid
        // (Vulkan requires unique binding numbers within a set) and crashed the real
        // DesktopVK native draw path with an AccessViolationException — confirmed via
        // a minimal repro isolating this exact shape. The wrapper must treat "same raw
        // binding" as combined regardless of the SPIR-V type, matching what a working
        // real-mgfxc compile of the same source produces.
        var texture = new TextureReflection
        {
            Name = "SpriteTexture", BindSlot = 0, Dimension = TextureDimension.Texture2D,
            RawBinding = 32,
        };
        var sampler = new SamplerReflection
        {
            Name = "SpriteTextureSampler", BindSlot = 0, RawBinding = 32, IsCombined = false,
        };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: null,
            textures: new[] { texture }, samplers: new[] { sampler });

        var r = new R(wrapped);
        r.Int32(); r.UInt32(); r.UInt32(); r.UInt32();
        for (int i = 0; i < 16; i++) r.UInt32();
        r.UInt32().ShouldBe(1u, customMessage: "same raw binding must combine into ONE entry, not two duplicate-binding entries");
        r.UInt32().ShouldBe(32u, customMessage: "shared RawBinding");
        r.UInt32().ShouldBe(1u, customMessage: "descriptorType == COMBINED_IMAGE_SAMPLER");
    }

    [Fact]
    public void Wrap_TextureDimension_MapsToMonoGameTextureType()
    {
        var cube = new TextureReflection { Name = "c", BindSlot = 5, Dimension = TextureDimension.TextureCube, RawBinding = 0 };

        byte[] wrapped = VulkanShaderCodeWrapper.Wrap(
            Spirv, ShaderStage.Pixel, constantBuffer: null,
            textures: new[] { cube }, samplers: Array.Empty<SamplerReflection>());

        var r = new R(wrapped);
        r.Int32(); r.UInt32(); r.UInt32(); r.UInt32();
        var textureTypes = new uint[16];
        for (int i = 0; i < 16; i++) textureTypes[i] = r.UInt32();
        textureTypes[5].ShouldBe(2u, customMessage: "MGTextureType Cube == 2");
    }
}
