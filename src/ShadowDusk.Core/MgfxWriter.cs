#nullable enable

using System;
using System.IO;
using System.Text;

namespace ShadowDusk.Core;

public sealed class MgfxWriter
{
    // On-disk MonoGame signature: the four ASCII bytes "MGFX" (NOT a reversed
    // uint). MonoGame's EffectReader reads these as an int and rejects anything
    // else, so byte order matters — this is the literal sequence it expects.
    private static readonly byte[] MgfxSignatureBytes = { 0x4D, 0x47, 0x46, 0x58 };

    public Result<byte[], ShaderError> Write(ShaderIR ir, MgfxWriterOptions options)
    {
        foreach (var cb in ir.ConstantBuffers)
        {
            if (cb.SizeInBytes > short.MaxValue)
                return Result<byte[], ShaderError>.Fail(new ShaderError(
                    File: "", Line: 0, Column: 0, Code: "SD0020",
                    Message: $"Constant buffer '{cb.Name}' size {cb.SizeInBytes} exceeds MGFX int16 maximum ({short.MaxValue})"));
        }
        // Guard every count/index serialized as a single byte (SD0022) so an oversized
        // effect fails loudly instead of silently truncating into a corrupt .mgfx —
        // the same policy as the SD0020/SD0021 int16 guards above.
        for (int i = 0; i < ir.Shaders.Count; i++)
        {
            var blob = ir.Shaders[i];
            if (blob.Samplers.Count > byte.MaxValue)
                return Result<byte[], ShaderError>.Fail(new ShaderError(
                    File: "", Line: 0, Column: 0, Code: "SD0022",
                    Message: $"Shader #{i} has {blob.Samplers.Count} samplers, exceeding the MGFX byte maximum ({byte.MaxValue})"));
            foreach (var s in blob.Samplers)
                if (s.Parameter is < byte.MinValue or > byte.MaxValue)
                    return Result<byte[], ShaderError>.Fail(new ShaderError(
                        File: "", Line: 0, Column: 0, Code: "SD0022",
                        Message: $"Shader #{i} sampler '{s.Name}' references parameter index {s.Parameter}, outside the MGFX byte range (0-{byte.MaxValue})"));
            if (blob.ConstantBufferIndices.Count > byte.MaxValue)
                return Result<byte[], ShaderError>.Fail(new ShaderError(
                    File: "", Line: 0, Column: 0, Code: "SD0022",
                    Message: $"Shader #{i} references {blob.ConstantBufferIndices.Count} constant buffers, exceeding the MGFX byte maximum ({byte.MaxValue})"));
            foreach (int cbi in blob.ConstantBufferIndices)
                if (cbi is < byte.MinValue or > byte.MaxValue)
                    return Result<byte[], ShaderError>.Fail(new ShaderError(
                        File: "", Line: 0, Column: 0, Code: "SD0022",
                        Message: $"Shader #{i} references constant-buffer index {cbi}, outside the MGFX byte range (0-{byte.MaxValue})"));
            if (blob.Attributes.Count > byte.MaxValue)
                return Result<byte[], ShaderError>.Fail(new ShaderError(
                    File: "", Line: 0, Column: 0, Code: "SD0022",
                    Message: $"Shader #{i} has {blob.Attributes.Count} vertex attributes, exceeding the MGFX byte maximum ({byte.MaxValue})"));
        }

        foreach (var tech in ir.Techniques)
            foreach (var pass in tech.Passes)
            {
                if (pass.VertexShaderIndex > short.MaxValue)
                    return Result<byte[], ShaderError>.Fail(new ShaderError(
                        File: "", Line: 0, Column: 0, Code: "SD0021",
                        Message: $"Pass '{pass.Name}' vertex shader index {pass.VertexShaderIndex} exceeds MGFX int16 maximum"));
                if (pass.PixelShaderIndex > short.MaxValue)
                    return Result<byte[], ShaderError>.Fail(new ShaderError(
                        File: "", Line: 0, Column: 0, Code: "SD0021",
                        Message: $"Pass '{pass.Name}' pixel shader index {pass.PixelShaderIndex} exceeds MGFX int16 maximum"));
            }

        // Serialize the body first so the header's EffectKey can be derived from it.
        using var bodyMs = new MemoryStream();
        using (var bodyBw = new BinaryWriter(bodyMs, Encoding.UTF8, leaveOpen: true))
        {
            WriteConstantBuffers(bodyBw, ir);
            WriteShaders(bodyBw, ir);
            WriteParameters(bodyBw, ir);
            WriteTechniques(bodyBw, ir);
            bodyBw.Flush();
        }
        byte[] body = bodyMs.ToArray();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteHeader(bw, options, ComputeEffectKey(body));
        bw.Write(body);
        bw.Write(MgfxSignatureBytes); // trailing "MGFX" footer MonoGame validates

        bw.Flush();
        return Result<byte[], ShaderError>.Ok(ms.ToArray());
    }

    private static void WriteHeader(BinaryWriter bw, MgfxWriterOptions options, uint effectKey)
    {
        bw.Write(MgfxSignatureBytes);    // 4 bytes "MGFX"
        bw.Write(options.MgfxVersion);   // 1 byte
        bw.Write((byte)options.Profile); // 1 byte
        bw.Write(effectKey);             // 4 bytes — MonoGame's effect-cache key
    }

    // Deterministic per-effect key. MonoGame uses it only as a cache key (mgfxc
    // writes an MD5-derived int); hashing the body gives distinct effects distinct
    // keys while keeping identical input -> identical output (constraint #3).
    //
    // Uses ManagedMd5 (not System.Security.Cryptography.MD5) because the .NET 8
    // browser/WASM runtime does NOT provide MD5 — the BCL MD5 throws
    // Cryptography_UnknownHashAlgorithm there, which broke the faithful in-browser
    // compile pipeline. ManagedMd5 is byte-identical to the BCL MD5 on every
    // platform, so desktop output and the cross-host byte-identity are unchanged.
    private static uint ComputeEffectKey(byte[] body)
    {
        byte[] hash = ManagedMd5.HashData(body);
        return BitConverter.ToUInt32(hash);
    }

    private static void WriteConstantBuffers(BinaryWriter bw, ShaderIR ir)
    {
        bw.Write(ir.ConstantBuffers.Count);
        foreach (var cb in ir.ConstantBuffers)
        {
            bw.Write(cb.Name);
            bw.Write((short)cb.SizeInBytes);
            bw.Write(cb.ParameterIndices.Count);
            // MonoGame reads these INTERLEAVED: per parameter an int32 index
            // immediately followed by a uint16 offset (not two grouped arrays).
            for (int i = 0; i < cb.ParameterIndices.Count; i++)
            {
                bw.Write(cb.ParameterIndices[i]);
                bw.Write(cb.ParameterOffsets[i]);
            }
        }
    }

    private static void WriteShaders(BinaryWriter bw, ShaderIR ir)
    {
        bw.Write(ir.Shaders.Count);
        foreach (var blob in ir.Shaders)
        {
            bw.Write(blob.Stage == ShaderStage.Vertex); // isVertexShader (bool)
            bw.Write(blob.Bytes.Length);
            bw.Write(blob.Bytes);

            // Sampler table (count is a byte).
            bw.Write((byte)blob.Samplers.Count);
            foreach (var s in blob.Samplers)
            {
                bw.Write(s.Type);
                bw.Write(s.TextureSlot);
                bw.Write(s.SamplerSlot);
                if (s.State is { } st)
                {
                    // Baked sampler_state members (Phase 43, F9), in the exact field
                    // order MonoGame 3.8.2's Shader reader consumes (== mgfxc's
                    // ShaderData.writer.cs). MonoGame applies these at EffectPass.Apply.
                    bw.Write(true);
                    bw.Write(st.AddressU);
                    bw.Write(st.AddressV);
                    bw.Write(st.AddressW);
                    bw.Write(st.BorderColorR);
                    bw.Write(st.BorderColorG);
                    bw.Write(st.BorderColorB);
                    bw.Write(st.BorderColorA);
                    bw.Write(st.Filter);
                    bw.Write(st.MaxAnisotropy);
                    bw.Write(st.MaxMipLevel);
                    bw.Write(st.MipMapLevelOfDetailBias);
                }
                else
                {
                    bw.Write(false); // sampler state comes from GraphicsDevice.SamplerStates
                }
                bw.Write(s.Name);
                bw.Write((byte)s.Parameter);
            }

            // Constant-buffer index list (count is a byte; indices are bytes).
            bw.Write((byte)blob.ConstantBufferIndices.Count);
            foreach (var cbi in blob.ConstantBufferIndices)
                bw.Write((byte)cbi);

            // Vertex-attribute table. Written for BOTH profiles: the count byte is
            // always emitted (0 for DirectX, which binds vertex inputs via the DXBC
            // input signature; populated only for GL vertex shaders).
            bw.Write((byte)blob.Attributes.Count);
            foreach (var a in blob.Attributes)
            {
                bw.Write(a.Name);
                bw.Write(a.Usage);
                bw.Write(a.Index);
                bw.Write(a.Location); // int16
            }
        }
    }

    private static void WriteParameters(BinaryWriter bw, ShaderIR ir)
    {
        bw.Write(ir.Parameters.Count);
        foreach (var p in ir.Parameters)
        {
            bw.Write(p.Class);
            bw.Write(p.Type);
            bw.Write(p.Name);
            bw.Write(p.Semantic ?? "");
            WriteAnnotations(bw, p.Annotations);
            bw.Write(p.RowCount);
            bw.Write(p.ColumnCount);
            WriteInt32List(bw, p.MemberIndices);
            WriteInt32List(bw, p.ElementIndices);

            // MonoGame reads a raw default-value blob for value-typed params
            // (Scalar/Vector/Matrix) that are neither structs nor arrays:
            // rows*cols*4 bytes, NO length prefix. Object/Struct/array params
            // carry none. We emit zeros — the runtime sets values by name.
            bool isValueType = p.Class <= 2; // Scalar=0, Vector=1, Matrix=2
            if (isValueType && p.MemberIndices.Count == 0 && p.ElementIndices.Count == 0)
            {
                int dataLen = p.RowCount * p.ColumnCount * 4;
                for (int b = 0; b < dataLen; b++)
                    bw.Write((byte)0);
            }
        }
    }

    private static void WriteTechniques(BinaryWriter bw, ShaderIR ir)
    {
        // Source order must be preserved — DO NOT sort
        bw.Write(ir.Techniques.Count);
        foreach (var tech in ir.Techniques)
        {
            bw.Write(tech.Name);
            WriteAnnotations(bw, tech.Annotations);
            bw.Write(tech.Passes.Count);
            foreach (var pass in tech.Passes)
            {
                bw.Write(pass.Name);
                WriteAnnotations(bw, pass.Annotations);
                bw.Write(pass.VertexShaderIndex); // int32 (MonoGame reads ReadInt32)
                bw.Write(pass.PixelShaderIndex);  // int32
                WriteRenderStateBlock(bw, pass.RenderState);
            }
        }
    }

    // ----------------------------------------------------------------------------
    // Pass render-state blocks (Phase 43, F1).
    //
    // MonoGame 3.8.2's Effect.ReadPasses reads each optional state object as a
    // boolean presence flag followed by a FIXED field sequence (alphabetical
    // property order, exactly mirroring mgfxc's EffectObject.writer.cs) — there are
    // no field IDs and no terminator. Unset fields get mgfxc's defaults: mgfxc's
    // TPGParser materializes `new BlendState()` / `new DepthStencilState()` /
    // `new RasterizerState()` and overwrites only the parsed keys, so the defaults
    // below are those state objects' constructor values (MonoGame v3.8.2
    // Graphics/States/{TargetBlendState,BlendState,DepthStencilState,RasterizerState}.cs).
    // ----------------------------------------------------------------------------

    private static void WriteRenderStateBlock(BinaryWriter bw, RenderStateBlock block)
    {
        WriteBlendState(bw, block);
        WriteDepthStencilState(bw, block);
        WriteRasterizerState(bw, block);
    }

    /// <summary>
    /// mgfxc's PassInfo.ToAlphaBlend (v3.8.2): the alpha-channel blend derived from a
    /// color blend when the .fx sets only the D3D9-style SrcBlend/DestBlend keys.
    /// </summary>
    private static BlendValue ToAlphaBlend(BlendValue blend) => blend switch
    {
        BlendValue.SourceColor             => BlendValue.SourceAlpha,
        BlendValue.InverseSourceColor      => BlendValue.InverseSourceAlpha,
        BlendValue.DestinationColor        => BlendValue.DestinationAlpha,
        BlendValue.InverseDestinationColor => BlendValue.InverseDestinationAlpha,
        _ => blend,
    };

    private static void WriteBlendState(BinaryWriter bw, RenderStateBlock block)
    {
        if (!block.HasBlendState)
        {
            bw.Write(false);
            return;
        }
        bw.Write(true);

        // mgfxc PassInfo semantics:
        //  - AlphaBlendEnable=TRUE (with no Src/DestBlend keys) presets the
        //    premultiplied pair One/InverseSourceAlpha on both channels;
        //    AlphaBlendEnable=FALSE forces One/Zero (== the BlendState defaults).
        //  - SrcBlend/DestBlend set the color blend AND the ToAlphaBlend-derived
        //    alpha blend; the explicit SrcBlendAlpha/DestBlendAlpha keys (a
        //    ShadowDusk extension mgfxc does not parse) override the derived alpha.
        BlendValue presetSrc = BlendValue.One;
        BlendValue presetDst = block.AlphaBlendEnable == true ? BlendValue.InverseSourceAlpha : BlendValue.Zero;

        BlendValue colorSrc = block.ColorSourceBlend      ?? presetSrc;
        BlendValue colorDst = block.ColorDestinationBlend ?? presetDst;
        BlendValue alphaSrc = block.AlphaSourceBlend
            ?? (block.ColorSourceBlend is { } cs ? ToAlphaBlend(cs) : presetSrc);
        BlendValue alphaDst = block.AlphaDestinationBlend
            ?? (block.ColorDestinationBlend is { } cd ? ToAlphaBlend(cd) : presetDst);

        // mgfxc quirk, mirrored for drop-in fidelity: PassInfo.BlendOp assigns ONLY
        // AlphaBlendFunction — ColorBlendFunction always ships as Add. The explicit
        // BlendOpAlpha extension key wins over BlendOp when both are present.
        BlendFunctionValue alphaFunc =
            block.AlphaBlendFunction ?? block.ColorBlendFunction ?? BlendFunctionValue.Add;
        const BlendFunctionValue colorFunc = BlendFunctionValue.Add;

        // ColorWriteChannels default: ColorWriteChannels.All == 15 (all four targets).
        int cwc0 = block.ColorWriteChannels  ?? 15;
        int cwc1 = block.ColorWriteChannels1 ?? 15;
        int cwc2 = block.ColorWriteChannels2 ?? 15;
        int cwc3 = block.ColorWriteChannels3 ?? 15;

        // BlendFactor is parsed as a D3DCOLOR dword (0xAARRGGBB); the reader consumes
        // R,G,B,A bytes. Default: Color.White (BlendState ctor).
        uint bf = block.BlendFactor ?? 0xFFFFFFFFu;

        // MultiSampleMask default: Int32.MaxValue (BlendState ctor).
        int multiSampleMask = block.MultiSampleMask.HasValue
            ? unchecked((int)block.MultiSampleMask.Value)
            : int.MaxValue;

        bw.Write((byte)alphaFunc);            // AlphaBlendFunction
        bw.Write((byte)alphaDst);             // AlphaDestinationBlend
        bw.Write((byte)alphaSrc);             // AlphaSourceBlend
        bw.Write((byte)((bf >> 16) & 0xFF));  // BlendFactor.R
        bw.Write((byte)((bf >> 8)  & 0xFF));  // BlendFactor.G
        bw.Write((byte)(bf         & 0xFF));  // BlendFactor.B
        bw.Write((byte)((bf >> 24) & 0xFF));  // BlendFactor.A
        bw.Write((byte)colorFunc);            // ColorBlendFunction
        bw.Write((byte)colorDst);             // ColorDestinationBlend
        bw.Write((byte)colorSrc);             // ColorSourceBlend
        bw.Write((byte)cwc0);                 // ColorWriteChannels
        bw.Write((byte)cwc1);                 // ColorWriteChannels1
        bw.Write((byte)cwc2);                 // ColorWriteChannels2
        bw.Write((byte)cwc3);                 // ColorWriteChannels3
        bw.Write(multiSampleMask);            // MultiSampleMask (int32)
    }

    private static void WriteDepthStencilState(BinaryWriter bw, RenderStateBlock block)
    {
        if (!block.HasDepthStencilState)
        {
            bw.Write(false);
            return;
        }
        bw.Write(true);

        bw.Write((byte)(block.CounterClockwiseStencilDepthBufferFail ?? StencilOperationValue.Keep));
        bw.Write((byte)(block.CounterClockwiseStencilFail            ?? StencilOperationValue.Keep));
        bw.Write((byte)(block.CounterClockwiseStencilFunction        ?? CompareFunctionValue.Always));
        bw.Write((byte)(block.CounterClockwiseStencilPass            ?? StencilOperationValue.Keep));
        bw.Write(block.DepthBufferEnable ?? true);                            // bool
        bw.Write((byte)(block.DepthBufferFunction ?? CompareFunctionValue.LessEqual));
        bw.Write(block.DepthBufferWriteEnable ?? true);                       // bool
        bw.Write(block.ReferenceStencil ?? 0);                                // int32
        bw.Write((byte)(block.StencilDepthBufferFail ?? StencilOperationValue.Keep));
        bw.Write(block.StencilEnable ?? false);                               // bool
        bw.Write((byte)(block.StencilFail     ?? StencilOperationValue.Keep));
        bw.Write((byte)(block.StencilFunction ?? CompareFunctionValue.Always));
        bw.Write(block.StencilMask ?? int.MaxValue);                          // int32
        bw.Write((byte)(block.StencilPass ?? StencilOperationValue.Keep));
        bw.Write(block.StencilWriteMask ?? int.MaxValue);                     // int32
        bw.Write(block.TwoSidedStencilMode ?? false);                         // bool
    }

    private static void WriteRasterizerState(BinaryWriter bw, RenderStateBlock block)
    {
        if (!block.HasRasterizerState)
        {
            bw.Write(false);
            return;
        }
        bw.Write(true);

        bw.Write((byte)(block.CullMode ?? CullModeValue.CullCounterClockwiseFace));
        bw.Write(block.DepthBias ?? 0f);                                      // single
        bw.Write((byte)(block.FillMode ?? FillModeValue.Solid));
        bw.Write(block.MultiSampleAntiAlias ?? true);                         // bool
        bw.Write(block.ScissorTestEnable ?? false);                           // bool
        bw.Write(block.SlopeScaleDepthBias ?? 0f);                            // single
    }

    private static void WriteAnnotations(BinaryWriter bw, IReadOnlyList<AnnotationInfo> annotations)
    {
        // MGFX v10 annotations are the int32 count and NOTHING else (Phase 43, F2).
        // MonoGame 3.8.2's ReadAnnotations reads only the count ("TODO: Annotations
        // are not implemented!") and never any bodies; mgfxc likewise writes none
        // (its annotation_handles are always null). Writing name/type/value bodies
        // here desynced the reader stream and bricked any annotated effect.
        bw.Write(annotations.Count);
    }

    private static void WriteInt32List(BinaryWriter bw, IReadOnlyList<int> list)
    {
        bw.Write(list.Count);
        foreach (var v in list)
            bw.Write(v);
    }
}
