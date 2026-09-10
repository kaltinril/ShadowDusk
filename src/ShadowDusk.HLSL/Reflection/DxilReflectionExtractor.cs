#nullable enable

using System.Runtime.InteropServices;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Reflection.Interop;
using Vortice.Dxc;
using static Vortice.Dxc.Dxc;

namespace ShadowDusk.HLSL.Reflection;

/// <summary>
/// Reflects Shader-Model-6 DXIL bytecode via <c>ID3D12ShaderReflection</c> (the DXC
/// reflection API), producing a <see cref="ReflectedEffect"/>. This is the native "oracle"
/// the pure-managed <see cref="ShadowDusk.Core.Reflection.SpirvReflector"/> is validated
/// against; the desktop OpenGL path uses it (the reflection runs inside the bundled
/// <c>dxcompiler</c>, so it is cross-platform), while the WASM host uses the managed reflector.
/// <para>
/// The reflection object comes from <c>IDxcUtils::CreateReflection</c> (Vortice.Dxc); its
/// managed projection is ShadowDusk's own (<c>Reflection/Interop</c>, Phase 63), not
/// <c>Vortice.Direct3D12</c>'s, because that assembly carries one type the CLR cannot load and
/// MonoGame 3.8.5's Content Builder scans every consumer dependency with an unguarded
/// <c>Assembly.GetTypes()</c>. Same DXC, same call, same bytes.
/// </para>
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
        catch (OperationCanceledException)
        {
            // Cancellation is not a reflection failure. ExtractCore's own
            // ThrowIfCancellationRequested lands in the catch-all below otherwise, turning
            // a cancelled token into an SD0102 "Reflection failed" compile error: the CLI's
            // watchdog then never reports X0007 "Compilation timed out", and a library
            // consumer's own CTS stops behaving per the .NET contract. Matches
            // DxbcReflectionExtractor and the guards in CompilationPipeline.
            throw;
        }
        catch (Exception ex)
        {
            return Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
                File:    "",
                Line:    0,
                Column:  0,
                Code:    "SD0102",
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
                    Code:    "SD0102",
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
                    Code:    "SD0102",
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
        D3D12ShaderDesc shaderDesc = reflection.GetDesc();

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
        D3D12ShaderDesc shaderDesc)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < shaderDesc.BoundResources; i++)
        {
            D3D12ShaderInputBindDesc bindDesc = reflection.GetResourceBindingDesc(i);
            if (bindDesc.Type == D3DShaderInputType.ConstantBuffer)
                slots[bindDesc.Name] = bindDesc.BindPoint;
        }
        return slots;
    }

    private static IReadOnlyList<ConstantBufferReflection> ExtractConstantBuffers(
        ID3D12ShaderReflection reflection,
        D3D12ShaderDesc shaderDesc,
        Dictionary<string, int> cbufferSlots)
    {
        var cbuffers = new List<ConstantBufferReflection>(shaderDesc.ConstantBuffers);

        for (int i = 0; i < shaderDesc.ConstantBuffers; i++)
        {
            ID3D12ShaderReflectionConstantBuffer cb = reflection.GetConstantBufferByIndex(i);
            D3D12ShaderBufferDesc cbDesc = cb.GetDesc();

            var variables = new List<VariableReflection>(cbDesc.VariableCount);
            for (int j = 0; j < cbDesc.VariableCount; j++)
            {
                ID3D12ShaderReflectionVariable variable = cb.GetVariableByIndex(j);
                D3D12ShaderVariableDesc varDesc = variable.GetDesc();
                ID3D12ShaderReflectionType varType = variable.GetVariableType();
                D3D12ShaderTypeDesc typeDesc = varType.GetDesc();

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
        D3D12ShaderTypeDesc typeDesc)
    {
        if (typeDesc.Class != D3DShaderVariableClass.Struct || typeDesc.MemberCount == 0)
            return null;

        var members = new List<VariableReflection>(typeDesc.MemberCount);
        for (int k = 0; k < typeDesc.MemberCount; k++)
        {
            ID3D12ShaderReflectionType memberType = type.GetMemberTypeByIndex(k);
            D3D12ShaderTypeDesc memberTypeDesc    = memberType.GetDesc();
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
        ExtractBoundResources(ID3D12ShaderReflection reflection, D3D12ShaderDesc shaderDesc)
    {
        var textures = new List<TextureReflection>();
        var samplers = new List<SamplerReflection>();

        for (int i = 0; i < shaderDesc.BoundResources; i++)
        {
            D3D12ShaderInputBindDesc bindDesc = reflection.GetResourceBindingDesc(i);

            switch (bindDesc.Type)
            {
                case D3DShaderInputType.Texture:
                    textures.Add(new TextureReflection
                    {
                        Name      = bindDesc.Name,
                        BindSlot  = bindDesc.BindPoint,
                        Dimension = MapSrvDimension(bindDesc.Dimension),
                    });
                    break;

                case D3DShaderInputType.Sampler:
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
            D3D12SignatureParameterDesc paramDesc = isInput
                ? reflection.GetInputParameterDesc(i)
                : reflection.GetOutputParameterDesc(i);

            parameters.Add(new SignatureParameterReflection
            {
                SemanticName  = paramDesc.SemanticName,
                SemanticIndex = paramDesc.SemanticIndex,
                Register      = paramDesc.Register,
                SystemValue   = paramDesc.SystemValueType.ToString(),
                ComponentType = paramDesc.ComponentType.ToString(),
                Mask          = paramDesc.UsageMask,
            });
        }

        return parameters;
    }

    // These delegate to the shared raw D3D value → enum tables in D3DReflectionMaps (the
    // interop structs carry the raw D3D_SHADER_VARIABLE_CLASS / _TYPE / D3D_SRV_DIMENSION
    // values), so the numeric mappings stay in lock-step with RdefReader. This path keeps its
    // own unmapped policy: an unmapped class/type throws (RdefReader reports failure instead).
    private static EffectParameterClass MapClass(uint cls) =>
        D3DReflectionMaps.TryMapClass(cls, out EffectParameterClass mapped)
            ? mapped
            : throw new InvalidOperationException($"Unmapped ShaderVariableClass: {cls}");

    private static EffectParameterType MapType(uint type) =>
        D3DReflectionMaps.TryMapType(type, out EffectParameterType mapped)
            ? mapped
            : throw new InvalidOperationException($"Unmapped ShaderVariableType: {type}");

    private static TextureDimension MapSrvDimension(uint dim) =>
        D3DReflectionMaps.MapSrvDimension(dim);
}
