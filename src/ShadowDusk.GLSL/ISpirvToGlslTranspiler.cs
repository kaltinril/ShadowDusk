#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.GLSL;

/// <summary>
/// Transpiles SPIR-V bytes to GLSL source. This is the injection seam that
/// alternative (e.g. WASM/browser) backends plug into in place of the native
/// SPIRV-Cross implementation, without changing the compilation pipeline.
/// </summary>
public interface ISpirvToGlslTranspiler
{
    Result<GlslSource, ShaderError> Transpile(
        ReadOnlyMemory<byte> spirvBytes,
        CancellationToken cancellationToken = default);
}
