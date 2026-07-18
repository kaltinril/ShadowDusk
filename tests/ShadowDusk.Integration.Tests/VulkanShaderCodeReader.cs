#nullable enable

namespace ShadowDusk.Integration.Tests;

/// <summary>
/// One Vulkan descriptor-layout binding entry, as <see cref="ShadowDusk.Core.VulkanShaderCodeWrapper"/>
/// writes it (field-for-field matching MonoGame 3.8.5's real Vulkan writer).
/// </summary>
public sealed record VulkanBindingRecord(
    uint Binding, uint DescriptorType, uint DescriptorCount, uint StageFlags, ulong ImmutableSamplers);

/// <summary>
/// Structurally parses a Vulkan-profile shader's <c>ShaderCode</c> field (the value
/// <see cref="ShadowDusk.Core.VulkanShaderCodeWrapper.Wrap"/> produces): the descriptor-layout
/// header MonoGame's real Vulkan writer prepends, plus the raw SPIR-V it wraps. The
/// C# counterpart to <c>validation/decode_mgfx_vulkan.py</c> — same "must consume every
/// byte" bar, usable directly from xUnit assertions.
/// </summary>
public sealed class VulkanShaderCodeReader
{
    private const uint SpirvMagic = 0x07230203;

    public int                             UniformBufferCount { get; }
    public uint                            UniformSlots       { get; }
    public uint                            TextureSlots       { get; }
    public uint                            SamplerSlots       { get; }
    public IReadOnlyList<uint>             TextureTypes       { get; }
    public IReadOnlyList<VulkanBindingRecord> Bindings         { get; }
    public byte[]                          Spirv              { get; }

    public bool SpirvMagicOk => Spirv.Length >= 4 && BitConverter.ToUInt32(Spirv, 0) == SpirvMagic;

    private VulkanShaderCodeReader(
        int uniformBufferCount, uint uniformSlots, uint textureSlots, uint samplerSlots,
        IReadOnlyList<uint> textureTypes, IReadOnlyList<VulkanBindingRecord> bindings, byte[] spirv)
    {
        UniformBufferCount = uniformBufferCount;
        UniformSlots       = uniformSlots;
        TextureSlots       = textureSlots;
        SamplerSlots       = samplerSlots;
        TextureTypes       = textureTypes;
        Bindings           = bindings;
        Spirv              = spirv;
    }

    public static VulkanShaderCodeReader Parse(byte[] shaderCode)
    {
        using var ms = new MemoryStream(shaderCode);
        using var br = new BinaryReader(ms);

        int uniformBufferCount = br.ReadInt32();
        uint uniformSlots = br.ReadUInt32();
        uint textureSlots = br.ReadUInt32();
        uint samplerSlots = br.ReadUInt32();

        var textureTypes = new uint[16];
        for (int i = 0; i < 16; i++)
            textureTypes[i] = br.ReadUInt32();

        uint bindingCount = br.ReadUInt32();
        var bindings = new List<VulkanBindingRecord>((int)bindingCount);
        for (int i = 0; i < bindingCount; i++)
        {
            bindings.Add(new VulkanBindingRecord(
                Binding:           br.ReadUInt32(),
                DescriptorType:    br.ReadUInt32(),
                DescriptorCount:   br.ReadUInt32(),
                StageFlags:        br.ReadUInt32(),
                ImmutableSamplers: br.ReadUInt64()));
        }

        byte[] spirv = br.ReadBytes((int)(ms.Length - ms.Position));

        return new VulkanShaderCodeReader(
            uniformBufferCount, uniformSlots, textureSlots, samplerSlots, textureTypes, bindings, spirv);
    }
}
