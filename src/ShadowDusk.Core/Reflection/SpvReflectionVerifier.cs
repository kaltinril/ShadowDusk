#nullable enable

namespace ShadowDusk.Core.Reflection;

public sealed class SpvReflectionVerifier
{
    public Result<BindingSlotMap, ShaderError> GetBindings(
        ReadOnlyMemory<byte> spirvBlob,
        CancellationToken ct = default)
    {
        // SPIRV-Cross P/Invoke not yet implemented (Phase 6).
        // Return empty map so the reflection pipeline can still run for DXIL-only use cases.
        return Result<BindingSlotMap, ShaderError>.Ok(BindingSlotMap.Empty);
    }
}
