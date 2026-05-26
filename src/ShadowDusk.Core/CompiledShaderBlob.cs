#nullable enable

namespace ShadowDusk.Core;

public sealed record CompiledShaderBlob(
    byte[]      Bytes,
    ShaderStage Stage
);
