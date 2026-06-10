#nullable enable

using System.Globalization;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;

namespace ShadowDusk.Compiler.Internal;

/// <summary>One technique's worth of FNA build input (names + compiled-shader indices).</summary>
internal sealed record Fx2TechniqueSource(string Name, IReadOnlyList<Fx2PassSource> Passes);

/// <summary>One pass's worth of FNA build input. Shader indices of -1 mean stage absent.</summary>
internal sealed record Fx2PassSource(
    string Name,
    int VertexShaderIndex,
    int PixelShaderIndex,
    RenderStateBlock RenderState);

/// <summary>
/// Pure assembly step of the FNA pipeline: merges the compiled SM1–3 shaders' CTAB
/// constant tables with the FX pre-parser's sampler metadata and the parsed render states
/// into the <see cref="Fx2EffectDesc"/> the <see cref="Fx2EffectWriter"/> emits.
///
/// The CTAB is the reflection source (it is what MojoShader itself binds against at load
/// time), so the parameter table contains exactly the uniforms/samplers the shaders
/// reference — by the names MojoShader will <c>strcmp</c> — plus the texture parameters the
/// sampler_state blocks bind. Globals the compiler optimized out are absent, mirroring the
/// MGFX writer's reflection-driven parameter table.
/// </summary>
internal static class Fx2EffectBuilder
{
    // Sampler-state ops (on-disk 164-based; docs/fx2-binary-format.md §8.2).
    private const int OpTexture = 164;
    private const int OpAddressU = 165;
    private const int OpAddressV = 166;
    private const int OpAddressW = 167;
    private const int OpBorderColor = 168;
    private const int OpMagFilter = 169;
    private const int OpMinFilter = 170;
    private const int OpMipFilter = 171;
    private const int OpMipMapLodBias = 172;
    private const int OpMaxMipLevel = 173;
    private const int OpMaxAnisotropy = 174;
    private const int OpSrgbTexture = 175;
    private const int OpElementIndex = 176;
    private const int OpDMapOffset = 177;

    // Render-state ops (MOJOSHADER_renderStateType file values, all in FNA's honored set).
    private const int RsZEnable = 0;
    private const int RsFillMode = 1;
    private const int RsZWriteEnable = 3;
    private const int RsSrcBlend = 6;
    private const int RsDestBlend = 7;
    private const int RsCullMode = 8;
    private const int RsZFunc = 9;
    private const int RsAlphaBlendEnable = 13;
    private const int RsStencilEnable = 22;
    private const int RsStencilFail = 23;
    private const int RsStencilZFail = 24;
    private const int RsStencilPass = 25;
    private const int RsStencilFunc = 26;
    private const int RsStencilRef = 27;
    private const int RsStencilMask = 28;
    private const int RsStencilWriteMask = 29;
    private const int RsMultiSampleAntiAlias = 67;
    private const int RsMultiSampleMask = 68;
    private const int RsColorWriteEnable = 73;
    private const int RsBlendOp = 75;
    private const int RsScissorTestEnable = 78;
    private const int RsSlopeScaleDepthBias = 79;
    private const int RsTwoSidedStencilMode = 88;
    private const int RsCcwStencilFail = 89;
    private const int RsCcwStencilZFail = 90;
    private const int RsCcwStencilPass = 91;
    private const int RsCcwStencilFunc = 92;
    private const int RsColorWriteEnable1 = 93;
    private const int RsColorWriteEnable2 = 94;
    private const int RsColorWriteEnable3 = 95;
    private const int RsBlendFactor = 96;
    private const int RsDepthBias = 98;
    private const int RsSeparateAlphaBlendEnable = 99;
    private const int RsSrcBlendAlpha = 100;
    private const int RsDestBlendAlpha = 101;
    private const int RsBlendOpAlpha = 102;

    public static Result<Fx2EffectDesc, ShaderError> Build(
        IReadOnlyList<Fx2TechniqueSource> techniques,
        IReadOnlyList<Fx2Shader> shaders,
        IReadOnlyList<CtabTable> ctabs,
        IReadOnlyList<SamplerInfo> samplerInfos,
        string sourceFile)
    {
        // ---- 1. Union the CTAB constants of every shader by name.
        var numericsByName = new Dictionary<string, CtabConstant>(StringComparer.Ordinal);
        var numericOrder = new List<string>();
        var samplersByName = new Dictionary<string, CtabConstant>(StringComparer.Ordinal);
        var samplerOrder = new List<string>();

        foreach (CtabTable ctab in ctabs)
        {
            foreach (CtabConstant constant in ctab.Constants)
            {
                if (constant.Class == 5 /* struct */)
                    return Fail(sourceFile,
                        $"global '{constant.Name}' is a struct; struct effect parameters are not " +
                        "supported for the FNA target — use individual globals");

                bool isSampler = constant.RegisterSet == CtabRegisterSet.Sampler;
                var byName = isSampler ? samplersByName : numericsByName;
                var order = isSampler ? samplerOrder : numericOrder;

                if (byName.TryGetValue(constant.Name, out CtabConstant? existing))
                {
                    // The same global reflected from another stage — shapes must agree.
                    if (existing.Class != constant.Class || existing.Type != constant.Type ||
                        existing.Rows != constant.Rows || existing.Columns != constant.Columns ||
                        existing.Elements != constant.Elements)
                    {
                        return Fail(sourceFile,
                            $"global '{constant.Name}' has conflicting shapes across shader stages");
                    }

                    if (existing.DefaultValue is null && constant.DefaultValue is not null)
                        byName[constant.Name] = existing with { DefaultValue = constant.DefaultValue };
                }
                else
                {
                    byName[constant.Name] = constant;
                    order.Add(constant.Name);
                }
            }
        }

        var samplerInfoByName = new Dictionary<string, SamplerInfo>(StringComparer.Ordinal);
        foreach (SamplerInfo info in samplerInfos)
            samplerInfoByName.TryAdd(info.Name, info);

        // ---- 2. Parameter list: numerics, then textures, then samplers — FNA requires
        // every texture to precede the samplers that reference it.
        var parameters = new List<Fx2Parameter>();

        foreach (string name in numericOrder)
        {
            CtabConstant c = numericsByName[name];
            parameters.Add(new Fx2Parameter
            {
                Name = c.Name,
                Class = c.Class,
                Type = c.Type,
                Rows = c.Rows,
                Columns = c.Columns,
                // CTAB writes 1 for non-arrays; on disk 0 means "not an array".
                Elements = c.Elements > 1 ? c.Elements : 0,
                DefaultValue = c.DefaultValue,
            });
        }

        var textureNames = new List<string>();
        foreach (string name in samplerOrder)
        {
            string? textureRef = samplerInfoByName.GetValueOrDefault(name)?.TextureReference;
            if (textureRef is not null && !textureNames.Contains(textureRef))
                textureNames.Add(textureRef);
        }

        foreach (string textureName in textureNames)
        {
            parameters.Add(new Fx2Parameter
            {
                Name = textureName,
                Class = 4,  // OBJECT
                Type = 5,   // TEXTURE — the undimensioned type; fxc may dimension it (F3, cosmetic)
            });
        }

        foreach (string name in samplerOrder)
        {
            CtabConstant c = samplersByName[name];

            // fx_2_0 sampler arrays need per-element value objects (§7.2) — unmodeled here.
            // Failing loudly beats silently emitting a non-array typedef whose parameter
            // shape diverges from fxc's.
            if (c.Elements > 1)
                return Fail(sourceFile,
                    $"sampler array '{name}[{c.Elements}]' is not supported for the FNA target — " +
                    "declare individual samplers");

            SamplerInfo? info = samplerInfoByName.GetValueOrDefault(name);

            var states = new List<Fx2SamplerState>();
            if (info?.TextureReference is not null)
            {
                states.Add(new Fx2SamplerState
                {
                    Operation = OpTexture,
                    TextureParameterName = info.TextureReference,
                });
            }

            if (info is not null)
            {
                foreach (SamplerStateEntry entry in info.StateEntries)
                {
                    Result<Fx2SamplerState, ShaderError> mapped =
                        MapSamplerState(entry, name, sourceFile);
                    if (mapped.IsFailure)
                        return Result<Fx2EffectDesc, ShaderError>.Fail(mapped.Error);
                    states.Add(mapped.Value);
                }
            }

            parameters.Add(new Fx2Parameter
            {
                Name = c.Name,
                Class = 4,                     // OBJECT
                Type = c.Type,                 // SAMPLER / SAMPLER2D / SAMPLERCUBE / … from CTAB
                SamplerStates = states,
            });
        }

        // ---- 3. Techniques: pass names/indices straight through; render states mapped
        // from the MonoGame-valued RenderStateBlock into the D3D9 domain fx_2_0 stores.
        var fx2Techniques = new List<Fx2Technique>(techniques.Count);
        foreach (Fx2TechniqueSource technique in techniques)
        {
            var passes = new List<Fx2Pass>(technique.Passes.Count);
            foreach (Fx2PassSource pass in technique.Passes)
            {
                // States FNA's Effect runtime throws NotImplementedException on at
                // EffectPass.Apply (the non-honored ops of docs/fx2-binary-format.md
                // §8.2). The fxc build of this same .fx would crash FNA at runtime —
                // silently dropping the state would mask that author error, so fail
                // loudly instead.
                if (pass.RenderState.KnownFnaThrowingStates.Count > 0)
                {
                    string offenders = string.Join(", ",
                        pass.RenderState.KnownFnaThrowingStates.Select(s => $"'{s}'"));
                    return Fail(sourceFile,
                        $"pass '{pass.Name}' sets render state(s) {offenders}, which FNA's " +
                        "Effect runtime throws NotImplementedException on at " +
                        "EffectPass.Apply — remove them from the pass and set the " +
                        "equivalent state from game code");
                }

                passes.Add(new Fx2Pass(
                    Name: pass.Name,
                    VertexShaderIndex: pass.VertexShaderIndex,
                    PixelShaderIndex: pass.PixelShaderIndex,
                    RenderStates: MapRenderStates(pass.RenderState)));
            }
            fx2Techniques.Add(new Fx2Technique(technique.Name, passes));
        }

        return Result<Fx2EffectDesc, ShaderError>.Ok(new Fx2EffectDesc
        {
            Parameters = parameters,
            Techniques = fx2Techniques,
            Shaders = shaders,
        });
    }

    // -------------------------------------------------------------------------
    // Sampler-state mapping: raw sampler_state key/value strings → fx_2_0 records.
    // -------------------------------------------------------------------------

    private static Result<Fx2SamplerState, ShaderError> MapSamplerState(
        SamplerStateEntry entry, string samplerName, string sourceFile)
    {
        string key = entry.Key;
        string value = entry.Value.Trim();

        if (Matches(key, "BorderColor") || Matches(key, "SRGBTexture") ||
            Matches(key, "ElementIndex") || Matches(key, "DMapOffset"))
        {
            return FailState(sourceFile, entry,
                $"sampler '{samplerName}' sets '{key}', which FNA's runtime throws " +
                "NotImplementedException on — remove it from the sampler_state block");
        }

        int op;
        if (Matches(key, "MinFilter")) op = OpMinFilter;
        else if (Matches(key, "MagFilter")) op = OpMagFilter;
        else if (Matches(key, "MipFilter")) op = OpMipFilter;
        else if (Matches(key, "AddressU")) op = OpAddressU;
        else if (Matches(key, "AddressV")) op = OpAddressV;
        else if (Matches(key, "AddressW")) op = OpAddressW;
        else if (Matches(key, "MipMapLodBias")) op = OpMipMapLodBias;
        else if (Matches(key, "MaxMipLevel")) op = OpMaxMipLevel;
        else if (Matches(key, "MaxAnisotropy")) op = OpMaxAnisotropy;
        else
        {
            return FailState(sourceFile, entry,
                $"sampler '{samplerName}' sets unrecognized sampler state '{key}' — " +
                "supported: MinFilter, MagFilter, MipFilter, AddressU/V/W, MipMapLodBias, " +
                "MaxMipLevel, MaxAnisotropy, Texture");
        }

        if (op == OpMipMapLodBias)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                return FailState(sourceFile, entry,
                    $"sampler '{samplerName}' state '{key}' has non-numeric value '{value}'");
            return Result<Fx2SamplerState, ShaderError>.Ok(
                new Fx2SamplerState { Operation = op, FloatValue = f });
        }

        int intValue;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            intValue = parsed;
        }
        else if (op is OpMinFilter or OpMagFilter or OpMipFilter)
        {
            // MOJOSHADER_textureFilterType / D3DTEXTUREFILTERTYPE.
            int? filter = value.ToUpperInvariant() switch
            {
                "NONE" => 0,
                "POINT" => 1,
                "LINEAR" => 2,
                "ANISOTROPIC" => 3,
                _ => null,
            };
            if (filter is null)
                return FailState(sourceFile, entry,
                    $"sampler '{samplerName}' state '{key}' has unrecognized filter '{value}' — " +
                    "supported: None, Point, Linear, Anisotropic");
            intValue = filter.Value;
        }
        else if (op is OpAddressU or OpAddressV or OpAddressW)
        {
            // MOJOSHADER_textureAddress / D3DTEXTUREADDRESS.
            int? address = value.ToUpperInvariant() switch
            {
                "WRAP" => 1,
                "MIRROR" => 2,
                "CLAMP" => 3,
                "BORDER" => 4,
                "MIRRORONCE" => 5,
                _ => null,
            };
            if (address is null)
                return FailState(sourceFile, entry,
                    $"sampler '{samplerName}' state '{key}' has unrecognized address mode '{value}' — " +
                    "supported: Wrap, Mirror, Clamp, Border, MirrorOnce");
            intValue = address.Value;
        }
        else
        {
            return FailState(sourceFile, entry,
                $"sampler '{samplerName}' state '{key}' has non-integer value '{value}'");
        }

        return Result<Fx2SamplerState, ShaderError>.Ok(
            new Fx2SamplerState { Operation = op, IntValue = intValue });
    }

    private static bool Matches(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // Render-state mapping. RenderStateBlock's enums carry MonoGame 3.8.2 ordinal
    // values, NOT D3D9 values — fx_2_0 state assignments need the D3D9 domain
    // (MOJOSHADER_blendMode/compareFunc/stencilOp/fillMode, identical to the native
    // D3D9 enums), so each enum gets an explicit value map here.
    // -------------------------------------------------------------------------

    internal static IReadOnlyList<Fx2RenderState> MapRenderStates(RenderStateBlock block)
    {
        var states = new List<Fx2RenderState>();

        void AddBool(int op, bool? value)
        {
            if (value.HasValue)
                states.Add(new Fx2RenderState(op, value.Value ? 1u : 0u));
        }

        void AddInt(int op, int? value)
        {
            if (value.HasValue)
                states.Add(new Fx2RenderState(op, (uint)value.Value));
        }

        void AddFloat(int op, float? value)
        {
            if (value.HasValue)
                states.Add(new Fx2RenderState(op, BitConverter.SingleToUInt32Bits(value.Value), IsFloat: true));
        }

        void AddDword(int op, uint? value)
        {
            if (value.HasValue)
                states.Add(new Fx2RenderState(op, value.Value));
        }

        // Blend.
        AddBool(RsAlphaBlendEnable, block.AlphaBlendEnable);
        if (block.ColorSourceBlend.HasValue)
            states.Add(new Fx2RenderState(RsSrcBlend, MapBlend(block.ColorSourceBlend.Value)));
        if (block.ColorDestinationBlend.HasValue)
            states.Add(new Fx2RenderState(RsDestBlend, MapBlend(block.ColorDestinationBlend.Value)));
        if (block.ColorBlendFunction.HasValue)
            states.Add(new Fx2RenderState(RsBlendOp, MapBlendFunction(block.ColorBlendFunction.Value)));
        AddBool(RsSeparateAlphaBlendEnable, block.SeparateAlphaBlendEnable);
        if (block.AlphaSourceBlend.HasValue)
            states.Add(new Fx2RenderState(RsSrcBlendAlpha, MapBlend(block.AlphaSourceBlend.Value)));
        if (block.AlphaDestinationBlend.HasValue)
            states.Add(new Fx2RenderState(RsDestBlendAlpha, MapBlend(block.AlphaDestinationBlend.Value)));
        if (block.AlphaBlendFunction.HasValue)
            states.Add(new Fx2RenderState(RsBlendOpAlpha, MapBlendFunction(block.AlphaBlendFunction.Value)));
        AddDword(RsBlendFactor, block.BlendFactor);           // D3DCOLOR dword, stored as-is
        AddInt(RsColorWriteEnable, block.ColorWriteChannels); // XNA channel bits == D3D9 bits
        AddInt(RsColorWriteEnable1, block.ColorWriteChannels1);
        AddInt(RsColorWriteEnable2, block.ColorWriteChannels2);
        AddInt(RsColorWriteEnable3, block.ColorWriteChannels3);

        // Depth/stencil.
        AddBool(RsZEnable, block.DepthBufferEnable);          // zBufferType FALSE=0 / TRUE=1
        AddBool(RsZWriteEnable, block.DepthBufferWriteEnable);
        if (block.DepthBufferFunction.HasValue)
            states.Add(new Fx2RenderState(RsZFunc, MapCompareFunction(block.DepthBufferFunction.Value)));
        AddBool(RsStencilEnable, block.StencilEnable);
        AddInt(RsStencilRef, block.ReferenceStencil);
        AddInt(RsStencilMask, block.StencilMask);
        AddInt(RsStencilWriteMask, block.StencilWriteMask);
        if (block.StencilFail.HasValue)
            states.Add(new Fx2RenderState(RsStencilFail, MapStencilOp(block.StencilFail.Value)));
        if (block.StencilDepthBufferFail.HasValue)
            states.Add(new Fx2RenderState(RsStencilZFail, MapStencilOp(block.StencilDepthBufferFail.Value)));
        if (block.StencilPass.HasValue)
            states.Add(new Fx2RenderState(RsStencilPass, MapStencilOp(block.StencilPass.Value)));
        if (block.StencilFunction.HasValue)
            states.Add(new Fx2RenderState(RsStencilFunc, MapCompareFunction(block.StencilFunction.Value)));
        AddBool(RsTwoSidedStencilMode, block.TwoSidedStencilMode);
        if (block.CounterClockwiseStencilFail.HasValue)
            states.Add(new Fx2RenderState(RsCcwStencilFail, MapStencilOp(block.CounterClockwiseStencilFail.Value)));
        if (block.CounterClockwiseStencilDepthBufferFail.HasValue)
            states.Add(new Fx2RenderState(RsCcwStencilZFail, MapStencilOp(block.CounterClockwiseStencilDepthBufferFail.Value)));
        if (block.CounterClockwiseStencilPass.HasValue)
            states.Add(new Fx2RenderState(RsCcwStencilPass, MapStencilOp(block.CounterClockwiseStencilPass.Value)));
        if (block.CounterClockwiseStencilFunction.HasValue)
            states.Add(new Fx2RenderState(RsCcwStencilFunc, MapCompareFunction(block.CounterClockwiseStencilFunction.Value)));

        // Rasterizer.
        if (block.CullMode.HasValue)
            states.Add(new Fx2RenderState(RsCullMode, (uint)block.CullMode.Value)); // already D3DCULL values
        if (block.FillMode.HasValue)
            states.Add(new Fx2RenderState(RsFillMode, block.FillMode.Value switch
            {
                FillModeValue.WireFrame => 2u, // D3DFILL_WIREFRAME
                _ => 3u,                       // D3DFILL_SOLID
            }));
        AddBool(RsScissorTestEnable, block.ScissorTestEnable);
        AddBool(RsMultiSampleAntiAlias, block.MultiSampleAntiAlias);
        AddDword(RsMultiSampleMask, block.MultiSampleMask);    // raw dword mask, stored as-is
        AddFloat(RsDepthBias, block.DepthBias);
        AddFloat(RsSlopeScaleDepthBias, block.SlopeScaleDepthBias);

        return states;
    }

    /// <summary>MonoGame Blend ordinal → D3DBLEND / MOJOSHADER_blendMode.</summary>
    private static uint MapBlend(BlendValue value) => value switch
    {
        BlendValue.Zero => 1,
        BlendValue.One => 2,
        BlendValue.SourceColor => 3,
        BlendValue.InverseSourceColor => 4,
        BlendValue.SourceAlpha => 5,
        BlendValue.InverseSourceAlpha => 6,
        BlendValue.DestinationAlpha => 7,
        BlendValue.InverseDestinationAlpha => 8,
        BlendValue.DestinationColor => 9,
        BlendValue.InverseDestinationColor => 10,
        BlendValue.SourceAlphaSaturation => 11,
        BlendValue.BlendFactor => 14,
        BlendValue.InverseBlendFactor => 15,
        _ => 2,
    };

    /// <summary>MonoGame BlendFunction ordinal → D3DBLENDOP / MOJOSHADER_blendOp.</summary>
    private static uint MapBlendFunction(BlendFunctionValue value) => value switch
    {
        BlendFunctionValue.Add => 1,
        BlendFunctionValue.Subtract => 2,
        BlendFunctionValue.ReverseSubtract => 3,
        BlendFunctionValue.Min => 4,
        BlendFunctionValue.Max => 5,
        _ => 1,
    };

    /// <summary>MonoGame CompareFunction ordinal → D3DCMP / MOJOSHADER_compareFunc.</summary>
    private static uint MapCompareFunction(CompareFunctionValue value) => value switch
    {
        CompareFunctionValue.Never => 1,
        CompareFunctionValue.Less => 2,
        CompareFunctionValue.Equal => 3,
        CompareFunctionValue.LessEqual => 4,
        CompareFunctionValue.Greater => 5,
        CompareFunctionValue.NotEqual => 6,
        CompareFunctionValue.GreaterEqual => 7,
        CompareFunctionValue.Always => 8,
        _ => 8,
    };

    /// <summary>MonoGame StencilOperation ordinal → D3DSTENCILOP / MOJOSHADER_stencilOp.</summary>
    private static uint MapStencilOp(StencilOperationValue value) => value switch
    {
        StencilOperationValue.Keep => 1,
        StencilOperationValue.Zero => 2,
        StencilOperationValue.Replace => 3,
        StencilOperationValue.IncrementSaturation => 4,
        StencilOperationValue.DecrementSaturation => 5,
        StencilOperationValue.Invert => 6,
        StencilOperationValue.Increment => 7,
        StencilOperationValue.Decrement => 8,
        _ => 1,
    };

    private static Result<Fx2EffectDesc, ShaderError> Fail(string sourceFile, string message) =>
        Result<Fx2EffectDesc, ShaderError>.Fail(new ShaderError(
            File: sourceFile,
            Line: 0,
            Column: 0,
            Code: "SD0303",
            Message: "FNA effect build failed: " + message));

    private static Result<Fx2SamplerState, ShaderError> FailState(
        string sourceFile, SamplerStateEntry entry, string message) =>
        Result<Fx2SamplerState, ShaderError>.Fail(new ShaderError(
            File: sourceFile,
            Line: entry.Span.StartLine,
            Column: entry.Span.StartColumn,
            Code: "SD0303",
            Message: "FNA effect build failed: " + message));
}
