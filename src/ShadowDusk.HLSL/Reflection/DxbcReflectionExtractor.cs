#nullable enable

using System.Runtime.Versioning;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// D3D11 (DXBC / Shader-Model-5) analogue of <see cref="DxilReflectionExtractor"/>.
/// Reflects SM5 DXBC bytecode via <c>ID3D11ShaderReflection</c> (the
/// d3dcompiler_47 reflection API) and produces the SAME <see cref="ReflectedEffect"/>
/// shape the DXIL path produces, so the downstream cbuffer/parameter assembly and
/// MGFX writer run unchanged. Windows-only at runtime (P/Invokes d3dcompiler_47.dll).
/// </summary>
public sealed class DxbcReflectionExtractor
{
    /// <summary>
    /// Reflects an SM5 DXBC module into a <see cref="ReflectedEffect"/>. Windows-only at
    /// runtime (P/Invokes <c>d3dcompiler_47.dll</c>).
    /// </summary>
    /// <param name="dxbcBlob">A complete SM5 DXBC module.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The reflected effect on success, or a <see cref="ShaderError"/> on failure.</returns>
    public Result<ReflectedEffect, ShaderError> Extract(
        ReadOnlyMemory<byte> dxbcBlob,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0210",
                Message: "DXBC reflection requires Windows (d3dcompiler_47); cross-platform DXBC is pending Phase 18 Track A"));
        }

        try
        {
            return ExtractCore(dxbcBlob, ct);
        }
        catch (Exception ex)
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0101",
                Message: "DXBC reflection failed: " + ex.Message));
        }
    }

    [SupportedOSPlatform("windows")]
    private static Result<ReflectedEffect, ShaderError> ExtractCore(
        ReadOnlyMemory<byte> dxbcBlob,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ID3D11ShaderReflection reflection = Compiler.Reflect<ID3D11ShaderReflection>(dxbcBlob.ToArray());
        try
        {
            return BuildReflectedEffect(reflection);
        }
        finally
        {
            reflection.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static Result<ReflectedEffect, ShaderError> BuildReflectedEffect(
        ID3D11ShaderReflection reflection)
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

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, int> BuildCbufferSlots(
        ID3D11ShaderReflection reflection,
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ConstantBufferReflection> ExtractConstantBuffers(
        ID3D11ShaderReflection reflection,
        ShaderDescription shaderDesc,
        Dictionary<string, int> cbufferSlots)
    {
        var cbuffers = new List<ConstantBufferReflection>(shaderDesc.ConstantBuffers);

        for (int i = 0; i < shaderDesc.ConstantBuffers; i++)
        {
            ID3D11ShaderReflectionConstantBuffer cb = reflection.GetConstantBufferByIndex(i);
            ConstantBufferDescription cbDesc = cb.Description;

            // mgfxc emits NO cbuffer record for a shader whose $Globals is empty
            // (e.g. a texture-only PS). Drop empty cbuffers so the DX .mgfx matches.
            if (cbDesc.VariableCount == 0)
                continue;

            var variables = new List<VariableReflection>(cbDesc.VariableCount);
            for (int j = 0; j < cbDesc.VariableCount; j++)
            {
                ID3D11ShaderReflectionVariable variable = cb.GetVariableByIndex(j);
                ShaderVariableDescription varDesc = variable.Description;
                ID3D11ShaderReflectionType varType = variable.VariableType;
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VariableReflection>? ExtractStructMembers(
        ID3D11ShaderReflectionType type,
        ShaderTypeDescription typeDesc)
    {
        if (typeDesc.Class != ShaderVariableClass.Struct || typeDesc.MemberCount == 0)
            return null;

        var members = new List<VariableReflection>(typeDesc.MemberCount);
        for (int k = 0; k < typeDesc.MemberCount; k++)
        {
            ID3D11ShaderReflectionType memberType = type.GetMemberTypeByIndex(k);
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

    [SupportedOSPlatform("windows")]
    private static (IReadOnlyList<TextureReflection>, IReadOnlyList<SamplerReflection>)
        ExtractBoundResources(ID3D11ShaderReflection reflection, ShaderDescription shaderDesc)
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

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SignatureParameterReflection> ExtractSignature(
        ID3D11ShaderReflection reflection,
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
