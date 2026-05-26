#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Internal;

internal static class ShaderIRBuilder
{
    public static ShaderIR Build(
        IReadOnlyList<CompiledShaderBlob> shaderBlobs,
        IReadOnlyList<MgfxTechniqueInfo> techniques,
        IReadOnlyList<ConstantBufferInfo> constantBuffers,
        IReadOnlyList<EffectParameterInfo> parameters)
    {
        return new ShaderIR
        {
            Shaders         = shaderBlobs,
            Techniques      = techniques,
            ConstantBuffers = constantBuffers,
            Parameters      = parameters,
        };
    }
}
