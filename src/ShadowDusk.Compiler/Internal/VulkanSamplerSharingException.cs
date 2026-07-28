#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// Thrown by <see cref="VulkanTextureSamplerBindingRewriter.Rewrite"/> when two DIFFERENT
/// textures sample through ONE sampler in the same live code path (not in mutually-exclusive
/// preprocessor branches). Vulkan's combined image-sampler binding model gives each texture
/// its own descriptor, and the pair co-location this rewriter enforces would put BOTH
/// textures on one binding — leaving the second texture unbound and rendering silently wrong
/// on DesktopVK (bug-hunt 2026-07-27 M5). Failing loudly here upholds design constraint 5.
///
/// <para>Mirrors the <c>MonoGameGlslRewriteException</c> pattern: the rewriter's
/// <c>string → string</c> signature is fixed by its <c>CompilationPipeline</c> call site, so
/// the decline travels as an exception carrying the located <see cref="ShaderError"/>;
/// <see cref="EffectCompiler.Compile"/> converts it back into the
/// <c>Result&lt;CompiledShader, ShaderError[]&gt;</c> contract.</para>
/// </summary>
internal sealed class VulkanSamplerSharingException : Exception
{
    /// <summary>The located SD0028 diagnostic describing the offending texture/sampler set.</summary>
    public ShaderError Error { get; }

    /// <summary>Creates the exception carrying the located diagnostic.</summary>
    /// <param name="error">The located SD0028 diagnostic.</param>
    public VulkanSamplerSharingException(ShaderError error) : base(error.Message)
        => Error = error;
}
