#nullable enable

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Derives a <see cref="ReflectedEffect"/> from a single compiled shader blob.
///
/// <para>Unlike the native DXIL path (<c>ID3D12ShaderReflection</c>), an implementation
/// of this interface is expected to be pure-managed so it can run inside the .NET WASM
/// browser host (Phase 19), where no native reflection library is available.</para>
///
/// <para>No stage parameter is taken: the execution model (vertex / pixel / …) is
/// recovered from the blob's own entry-point metadata.</para>
/// </summary>
public interface IShaderReflector
{
    /// <summary>
    /// Reflects a SPIR-V module into a <see cref="ReflectedEffect"/>.
    /// </summary>
    /// <param name="spirvBlob">A complete SPIR-V module (little-endian word stream).</param>
    Result<ReflectedEffect, ShaderError> Reflect(ReadOnlyMemory<byte> spirvBlob);
}
