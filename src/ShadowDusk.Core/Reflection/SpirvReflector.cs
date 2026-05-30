#nullable enable

using ShadowDusk.Core.Reflection.Spirv;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Pure-managed <see cref="IShaderReflector"/> that derives a <see cref="ReflectedEffect"/>
/// directly from SPIR-V bytecode — no native DXIL / <c>ID3D12ShaderReflection</c> path,
/// so it runs inside the .NET WASM browser host (Phase 19).
///
/// <para>It mirrors the field-population semantics of the native DXIL oracle
/// (<c>ShadowDusk.HLSL.Reflection.DxilReflectionExtractor</c>) for the OpenGL SM3 PS-only
/// corpus: constant-buffer layouts (offsets, sizes, class/type, rows/columns/elements,
/// 16-byte packing) and texture / sampler bind slots.</para>
///
/// <para><b>Signatures.</b> <see cref="ReflectedEffect.InputSignature"/> and
/// <see cref="ReflectedEffect.OutputSignature"/> are intentionally left EMPTY: SPIR-V
/// discards HLSL semantic strings (<c>TEXCOORD0</c>, <c>SV_Target</c>, …) — it keeps only
/// numeric <c>Location</c> decorations — so the original signatures cannot be recovered.
/// The PS-only MonoGame corpus does not need them for <c>.mgfx</c> output.</para>
/// </summary>
public sealed class SpirvReflector : IShaderReflector
{
    public Result<ReflectedEffect, ShaderError> Reflect(ReadOnlyMemory<byte> spirvBlob)
    {
        SpirvModule? module = SpirvModule.TryParse(spirvBlob.Span);
        if (module is null)
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0101",
                Message: "SPIR-V reflection failed: blob is not a valid SPIR-V module (bad magic or size)."));
        }

        try
        {
            var parser = new SpirvReflectionParser(module);
            var (constantBuffers, textures, samplers) = parser.Reflect();

            return Result<ReflectedEffect, ShaderError>.Ok(new ReflectedEffect
            {
                ConstantBuffers = constantBuffers,
                Textures        = textures,
                Samplers        = samplers,
                // SPIR-V cannot recover HLSL semantics — see class remarks.
                InputSignature  = Array.Empty<SignatureParameterReflection>(),
                OutputSignature = Array.Empty<SignatureParameterReflection>(),
                Parameters      = Array.Empty<ParameterReflection>(),
            });
        }
        catch (Exception ex)
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0101",
                Message: "SPIR-V reflection failed: " + ex.Message));
        }
    }
}
