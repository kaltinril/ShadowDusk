#nullable enable

using System.Runtime.InteropServices;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Vortice.Direct3D;
using Vortice.Direct3D12.Shader;
using Vortice.Dxc;
using static Vortice.Dxc.Dxc;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// Reflects Shader-Model-6 DXIL bytecode via <c>ID3D12ShaderReflection</c> (the DXC
/// reflection API), producing a <see cref="ReflectedEffect"/>. This is the native "oracle"
/// the pure-managed <see cref="ShadowDusk.Core.Reflection.SpirvReflector"/> is validated
/// against; the desktop OpenGL path uses it (the reflection runs inside the bundled
/// <c>dxcompiler</c>, so it is cross-platform), while the WASM host uses the managed reflector.
/// </summary>
public sealed class DxilReflectionExtractor
{
    /// <summary>
    /// Reflects a DXIL module into a <see cref="ReflectedEffect"/>.
    /// </summary>
    /// <param name="dxilBlob">A complete SM6 DXIL module.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<ReflectedEffect, ShaderError> Extract(
        ReadOnlyMemory<byte> dxilBlob,
        CancellationToken ct = default)
    {
        try
        {
            return ExtractCore(dxilBlob, ct);
        }
        catch (Exception ex)
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0100",
                Message: "Reflection failed: " + ex.Message));
        }
    }

    private static Result<ReflectedEffect, ShaderError> ExtractCore(
        ReadOnlyMemory<byte> dxilBlob,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // macOS: hook Vortice's ResolveLibrary so our pinned libdxcompiler.dylib
        // resolves (Phase 37 A). Idempotent; no-op on Windows/Linux.
        HLSL.Dxc.DxcLoader.Register();

        IDxcUtils utils = CreateDxcUtils();

        nint nativeBuffer = nint.Zero;
        try
        {
            // DXC_CP_ACP = 0 — binary data, no text encoding.
            byte[] bytes = dxilBlob.ToArray();
            nativeBuffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, nativeBuffer, bytes.Length);

            utils.CreateBlobFromPinned(nativeBuffer, bytes.Length, 0, out IDxcBlobEncoding? encodingBlob);
            if (encodingBlob is null)
            {
                return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                    File:    "",
                    Line:    0,
                    Column:  0,
                    Code:    "SD0100",
                    Message: "Reflection failed: unable to create DXC blob from DXIL bytes"));
            }

            utils.CreateReflection(encodingBlob, out ID3D12ShaderReflection? reflection);
            encodingBlob.Dispose();
            if (reflection is null)
            {
                return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                    File:    "",
                    Line:    0,
                    Column:  0,
                    Code:    "SD0100",
                    Message: "Reflection failed: CreateReflection returned null"));
            }

            try
            {
                return BuildReflectedEffect(reflection);
            }
            finally
            {
                reflection.Dispose();
            }
        }
        finally
        {
            if (nativeBuffer != nint.Zero)
                Marshal.FreeHGlobal(nativeBuffer);
            utils.Dispose();
        }
    }

    private static Result<ReflectedEffect, ShaderError> BuildReflectedEffect(
        ID3D12ShaderReflection reflection)
    {
        ShaderDescription shaderDesc = reflection.Description;

        Dictionary<string, int> cbufferSlots = BuildCbufferSlots(reflection, shaderDesc);

        IReadOnlyList<ConstantBufferReflection> constantBuffers =
            ExtractConstantBuffers(reflection, shaderDesc, cbufferSlots);

        (IReadOnlyList<TextureReflection> textures,
         IReadOnlyList<SamplerReflection>  samplers) =
            ExtractBoundResources(reflection, shaderDesc);

        IReadOnlyList<SignatureParameterReflection> inputSig =
            ExtractSignature(reflection, shaderDesc.InputParameters, isInput: true);

        IReadOnlyList<SignatureParameterReflection> outputSig =
            ExtractSignature(reflection, shaderDesc.OutputParameters, isInput: false);

        return Result<ReflectedEffect, ShaderError>.Ok(new ReflectedEffect
        {
            ConstantBuffers = constantBuffers,
            Textures        = textures,
            Samplers        = samplers,
            InputSignature  = inputSig,
            OutputSignature = outputSig,
            Parameters      = Array.Empty<ParameterReflection>(),
        });
    }

    private static Dictionary<string, int> BuildCbufferSlots(
        ID3D12ShaderReflection reflection,
        ShaderDescription shaderDesc)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < shaderDesc.BoundResources; i++)
        {
            InputBindingDescription bindDesc = reflection.GetResourceBindingDescription(i);
            if (bindDesc.Type == ShaderInputType.ConstantBuffer)
                slots[bindDesc.Name] = bindDesc.BindPoint;
        }
        return slots;
    }

    private static IReadOnlyList<ConstantBufferReflection> ExtractConstantBuffers(
        ID3D12ShaderReflection reflection,
        ShaderDescription shaderDesc,
        Dictionary<string, int> cbufferSlots)
    {
        var cbuffers = new List<ConstantBufferReflection>(shaderDesc.ConstantBuffers);

        for (int i = 0; i < shaderDesc.ConstantBuffers; i++)
        {
            ID3D12ShaderReflectionConstantBuffer cb = reflection.GetConstantBufferByIndex(i);
            ConstantBufferDescription cbDesc = cb.Description;

            var variables = new List<VariableReflection>(cbDesc.VariableCount);
            for (int j = 0; j < cbDesc.VariableCount; j++)
            {
                ID3D12ShaderReflectionVariable variable = cb.GetVariableByIndex(j);
                ShaderVariableDescription varDesc = variable.Description;
                ID3D12ShaderReflectionType varType = variable.VariableType;
                ShaderTypeDescription typeDesc = varType.Description;

                variables.Add(new VariableReflection
                {
                    Name           = varDesc.Name,
                    StartOffset    = varDesc.StartOffset,
                    SizeBytes      = typeDesc.ElementCount > 0
                                       ? (varDesc.Size + 15) & ~15
                                       : varDesc.Size,
                    ParameterClass = MapClass(typeDesc.Class),
                    ParameterType  = MapType(typeDesc.Type),
                    Rows           = typeDesc.RowCount,
                    Columns        = typeDesc.ColumnCount,
                    Elements       = typeDesc.ElementCount,
                    Members        = ExtractStructMembers(varType, typeDesc),
                });
            }

            cbuffers.Add(new ConstantBufferReflection
            {
                Name      = cbDesc.Name,
                SizeBytes = cbDesc.Size,
                BindSlot  = cbufferSlots.TryGetValue(cbDesc.Name, out int slot) ? slot : 0,
                Variables = variables,
            });
        }

        return cbuffers;
    }

    private static IReadOnlyList<VariableReflection>? ExtractStructMembers(
        ID3D12ShaderReflectionType type,
        ShaderTypeDescription typeDesc)
    {
        if (typeDesc.Class != ShaderVariableClass.Struct || typeDesc.MemberCount == 0)
            return null;

        var members = new List<VariableReflection>(typeDesc.MemberCount);
        for (int k = 0; k < typeDesc.MemberCount; k++)
        {
            ID3D12ShaderReflectionType memberType = type.GetMemberTypeByIndex(k);
            ShaderTypeDescription memberTypeDesc  = memberType.Description;
            string memberName = type.GetMemberTypeName(k);

            members.Add(new VariableReflection
            {
                Name           = memberName,
                StartOffset    = memberTypeDesc.Offset,
                SizeBytes      = 0,
                ParameterClass = MapClass(memberTypeDesc.Class),
                ParameterType  = MapType(memberTypeDesc.Type),
                Rows           = memberTypeDesc.RowCount,
                Columns        = memberTypeDesc.ColumnCount,
                Elements       = memberTypeDesc.ElementCount,
                Members        = ExtractStructMembers(memberType, memberTypeDesc),
            });
        }

        return members;
    }

    private static (IReadOnlyList<TextureReflection>, IReadOnlyList<SamplerReflection>)
        ExtractBoundResources(ID3D12ShaderReflection reflection, ShaderDescription shaderDesc)
    {
        var textures = new List<TextureReflection>();
        var samplers = new List<SamplerReflection>();

        for (int i = 0; i < shaderDesc.BoundResources; i++)
        {
            InputBindingDescription bindDesc = reflection.GetResourceBindingDescription(i);

            switch (bindDesc.Type)
            {
                case ShaderInputType.Texture:
                    textures.Add(new TextureReflection
                    {
                        Name      = bindDesc.Name,
                        BindSlot  = bindDesc.BindPoint,
                        Dimension = MapSrvDimension(bindDesc.Dimension),
                    });
                    break;

                case ShaderInputType.Sampler:
                    samplers.Add(new SamplerReflection
                    {
                        Name     = bindDesc.Name,
                        BindSlot = bindDesc.BindPoint,
                    });
                    break;
            }
        }

        return (textures, samplers);
    }

    private static IReadOnlyList<SignatureParameterReflection> ExtractSignature(
        ID3D12ShaderReflection reflection,
        int count,
        bool isInput)
    {
        var parameters = new List<SignatureParameterReflection>(count);

        for (int i = 0; i < count; i++)
        {
            ShaderParameterDescription paramDesc = isInput
                ? reflection.GetInputParameterDescription(i)
                : reflection.GetOutputParameterDescription(i);

            parameters.Add(new SignatureParameterReflection
            {
                SemanticName  = paramDesc.SemanticName,
                SemanticIndex = paramDesc.SemanticIndex,
                Register      = paramDesc.Register,
                SystemValue   = paramDesc.SystemValueType.ToString(),
                ComponentType = paramDesc.ComponentType.ToString(),
                Mask          = (byte)paramDesc.UsageMask,
            });
        }

        return parameters;
    }

    private static EffectParameterClass MapClass(ShaderVariableClass cls) =>
        cls switch
        {
            ShaderVariableClass.Scalar        => EffectParameterClass.Scalar,
            ShaderVariableClass.Vector        => EffectParameterClass.Vector,
            ShaderVariableClass.MatrixRows    => EffectParameterClass.Matrix,
            ShaderVariableClass.MatrixColumns => EffectParameterClass.Matrix,
            ShaderVariableClass.Object        => EffectParameterClass.Object,
            ShaderVariableClass.Struct        => EffectParameterClass.Struct,
            _ => throw new InvalidOperationException($"Unmapped ShaderVariableClass: {cls}"),
        };

    private static EffectParameterType MapType(ShaderVariableType type) =>
        type switch
        {
            ShaderVariableType.Void        => EffectParameterType.Void,
            ShaderVariableType.Bool        => EffectParameterType.Bool,
            ShaderVariableType.Int         => EffectParameterType.Int32,
            ShaderVariableType.UInt        => EffectParameterType.Int32,
            ShaderVariableType.Float       => EffectParameterType.Single,
            ShaderVariableType.String      => EffectParameterType.String,
            ShaderVariableType.Texture     => EffectParameterType.Texture,
            ShaderVariableType.Texture1D   => EffectParameterType.Texture1D,
            ShaderVariableType.Texture2D   => EffectParameterType.Texture2D,
            ShaderVariableType.Texture3D   => EffectParameterType.Texture3D,
            ShaderVariableType.TextureCube => EffectParameterType.TextureCube,
            _ => throw new InvalidOperationException($"Unmapped ShaderVariableType: {type}"),
        };

    private static TextureDimension MapSrvDimension(ShaderResourceViewDimension dim) =>
        dim switch
        {
            ShaderResourceViewDimension.Texture1D                  => TextureDimension.Texture1D,
            ShaderResourceViewDimension.Texture1DArray             => TextureDimension.Texture1D,
            ShaderResourceViewDimension.Texture2D                  => TextureDimension.Texture2D,
            ShaderResourceViewDimension.Texture2DArray             => TextureDimension.Texture2D,
            ShaderResourceViewDimension.Texture2DMultisampled      => TextureDimension.Texture2D,
            ShaderResourceViewDimension.Texture2DMultisampledArray => TextureDimension.Texture2D,
            ShaderResourceViewDimension.Texture3D                  => TextureDimension.Texture3D,
            ShaderResourceViewDimension.TextureCube                => TextureDimension.TextureCube,
            ShaderResourceViewDimension.TextureCubeArray           => TextureDimension.TextureCube,
            _                                                      => TextureDimension.Unknown,
        };
}
