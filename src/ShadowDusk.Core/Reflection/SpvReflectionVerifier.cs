#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Cross-checks texture and sampler bind slots against a SPIR-V module, producing a
/// <see cref="BindingSlotMap"/>. The SPIRV-Cross-backed verification is not yet implemented;
/// the current implementation returns <see cref="BindingSlotMap.Empty"/> so the reflection
/// pipeline can still run for DXIL-only use cases.
/// </summary>
/// <remarks>
/// Obsolete: nothing in ShadowDusk calls it, and it never checked anything. It will be removed in a
/// future release (issue #429).
/// </remarks>
[Obsolete("SpvReflectionVerifier is a no-op that always returns BindingSlotMap.Empty; nothing calls it. It will be removed in a future release (issue #429).")]
public sealed class SpvReflectionVerifier
{
    /// <summary>
    /// Extracts the texture/sampler binding slots from a SPIR-V module.
    /// </summary>
    /// <param name="spirvBlob">A complete SPIR-V module.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// A <see cref="BindingSlotMap"/>; currently always
    /// <see cref="BindingSlotMap.Empty"/> until SPIRV-Cross verification lands.
    /// </returns>
    public Result<BindingSlotMap, ShaderError> GetBindings(
        ReadOnlyMemory<byte> spirvBlob,
        CancellationToken ct = default)
    {
        // SPIRV-Cross P/Invoke not yet implemented (Phase 6).
        // Return empty map so the reflection pipeline can still run for DXIL-only use cases.
        return Result<BindingSlotMap, ShaderError>.Ok(BindingSlotMap.Empty);
    }
}
