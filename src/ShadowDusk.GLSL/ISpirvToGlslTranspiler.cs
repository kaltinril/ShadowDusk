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
    /// <summary>
    /// Transpiles a SPIR-V module to GLSL source.
    /// </summary>
    /// <param name="spirvBytes">A complete SPIR-V module (little-endian word stream).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The transpiled <see cref="GlslSource"/> on success, or a <see cref="ShaderError"/> on
    /// failure.
    /// </returns>
    Result<GlslSource, ShaderError> Transpile(
        ReadOnlyMemory<byte> spirvBytes,
        CancellationToken cancellationToken = default);
}
