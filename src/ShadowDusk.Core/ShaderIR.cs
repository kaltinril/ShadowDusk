#nullable enable

namespace ShadowDusk.Core;

public sealed class ShaderIR
{
    public IReadOnlyList<ConstantBufferInfo>  ConstantBuffers { get; init; } = [];
    public IReadOnlyList<CompiledShaderBlob>  Shaders         { get; init; } = [];
    public IReadOnlyList<EffectParameterInfo> Parameters      { get; init; } = [];
    public IReadOnlyList<MgfxTechniqueInfo>   Techniques      { get; init; } = [];
}
