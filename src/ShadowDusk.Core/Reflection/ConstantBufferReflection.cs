#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>A reflected constant (uniform) buffer: its name, size, bind slot, and variables.</summary>
public sealed record ConstantBufferReflection
{
    /// <summary>The constant buffer's name.</summary>
    public required string                        Name      { get; init; }
    /// <summary>The total size of the buffer in bytes.</summary>
    public required int                           SizeBytes { get; init; }
    /// <summary>The register/bind slot the buffer occupies.</summary>
    public required int                           BindSlot  { get; init; }
    /// <summary>
    /// The raw SPIR-V <c>Binding</c> decoration (unlike <see cref="BindSlot"/>, a
    /// class-relative renumbering). Used only by the Vulkan container's descriptor-layout
    /// table — Vulkan's cbuffer binding is NOT reliably 0; it must be reflected like any
    /// other resource. Default 0 for non-Vulkan.
    /// </summary>
    public int RawBinding { get; init; }
    /// <summary>
    /// The raw SPIR-V <c>DescriptorSet</c> decoration (see <see cref="RawBinding"/>), set by the
    /// SPIR-V reflection; 0 elsewhere. The Vulkan target also uses it to refuse a buffer outside its
    /// stage's descriptor set (<c>SD0229</c>), since MonoGame's DesktopVK binds one set per stage.
    /// </summary>
    public int RawDescriptorSet { get; init; }
    /// <summary>
    /// True when the buffer is a storage buffer (an HLSL <c>StructuredBuffer</c>, <c>ByteAddressBuffer</c>
    /// or one of their <c>RW</c> forms), not a constant buffer: a <c>BufferBlock</c> struct in the SPIR-V
    /// reflection; false from DXIL/DXBC. The Vulkan target refuses it (<c>SD0233</c>), MonoGame's
    /// Vulkan effect format having no storage-buffer descriptor.
    /// </summary>
    internal bool IsStorageBuffer { get; init; }
    /// <summary>The variables packed into the buffer, in offset order.</summary>
    public required IReadOnlyList<VariableReflection> Variables { get; init; }
}
