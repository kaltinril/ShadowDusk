#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// A placeholder that was meant to cross-check texture and sampler bind slots against a SPIR-V
/// module. It was never implemented and always returns <see cref="BindingSlotMap.Empty"/>.
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
    /// Always <see cref="BindingSlotMap.Empty"/>.
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
