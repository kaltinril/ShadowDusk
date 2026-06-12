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
    private const byte RenderStateSentinel = 0xFF;

    // EffectParameterType byte value for annotation dispatch
    private const byte TypeSingle = 3;
    private const byte TypeInt32  = 2;
    private const byte TypeBool   = 1;
    private const byte TypeString = 4;

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
                bw.Write((byte)0);  // hasState = false — sampler state comes from GraphicsDevice.SamplerStates
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

    private static void WriteRenderStateBlock(BinaryWriter bw, RenderStateBlock block)
    {
        WriteOptionalStateObject(bw, block.HasBlendState,        () => WriteBlendState(bw, block));
        WriteOptionalStateObject(bw, block.HasDepthStencilState, () => WriteDepthStencilState(bw, block));
        WriteOptionalStateObject(bw, block.HasRasterizerState,   () => WriteRasterizerState(bw, block));
    }

    private static void WriteOptionalStateObject(BinaryWriter bw, bool present, Action writeFields)
    {
        if (present)
        {
            bw.Write((byte)1);
            writeFields();
            bw.Write(RenderStateSentinel);
        }
        else
        {
            bw.Write((byte)0);
        }
    }

    private static void WriteBlendState(BinaryWriter bw, RenderStateBlock block)
    {
        if (block.AlphaBlendEnable.HasValue)
            WriteStateKV(bw, 0, block.AlphaBlendEnable.Value ? 1 : 0);
        if (block.ColorSourceBlend.HasValue)
            WriteStateKV(bw, 1, (int)block.ColorSourceBlend.Value);
        if (block.ColorDestinationBlend.HasValue)
            WriteStateKV(bw, 2, (int)block.ColorDestinationBlend.Value);
        if (block.ColorBlendFunction.HasValue)
            WriteStateKV(bw, 3, (int)block.ColorBlendFunction.Value);
        if (block.AlphaSourceBlend.HasValue)
            WriteStateKV(bw, 4, (int)block.AlphaSourceBlend.Value);
        if (block.AlphaDestinationBlend.HasValue)
            WriteStateKV(bw, 5, (int)block.AlphaDestinationBlend.Value);
        if (block.AlphaBlendFunction.HasValue)
            WriteStateKV(bw, 6, (int)block.AlphaBlendFunction.Value);
        if (block.ColorWriteChannels.HasValue)
            WriteStateKV(bw, 7, block.ColorWriteChannels.Value);
    }

    private static void WriteDepthStencilState(BinaryWriter bw, RenderStateBlock block)
    {
        if (block.DepthBufferEnable.HasValue)
            WriteStateKV(bw, 0, block.DepthBufferEnable.Value ? 1 : 0);
        if (block.DepthBufferWriteEnable.HasValue)
            WriteStateKV(bw, 1, block.DepthBufferWriteEnable.Value ? 1 : 0);
        if (block.DepthBufferFunction.HasValue)
            WriteStateKV(bw, 2, (int)block.DepthBufferFunction.Value);
        if (block.StencilEnable.HasValue)
            WriteStateKV(bw, 3, block.StencilEnable.Value ? 1 : 0);
        if (block.ReferenceStencil.HasValue)
            WriteStateKV(bw, 4, block.ReferenceStencil.Value);
        if (block.StencilMask.HasValue)
            WriteStateKV(bw, 5, block.StencilMask.Value);
        if (block.StencilWriteMask.HasValue)
            WriteStateKV(bw, 6, block.StencilWriteMask.Value);
        if (block.StencilFail.HasValue)
            WriteStateKV(bw, 7, (int)block.StencilFail.Value);
        if (block.StencilDepthBufferFail.HasValue)
            WriteStateKV(bw, 8, (int)block.StencilDepthBufferFail.Value);
        if (block.StencilPass.HasValue)
            WriteStateKV(bw, 9, (int)block.StencilPass.Value);
        if (block.StencilFunction.HasValue)
            WriteStateKV(bw, 10, (int)block.StencilFunction.Value);
    }

    private static void WriteRasterizerState(BinaryWriter bw, RenderStateBlock block)
    {
        if (block.CullMode.HasValue)
            WriteStateKV(bw, 0, (int)block.CullMode.Value);
        if (block.FillMode.HasValue)
            WriteStateKV(bw, 1, (int)block.FillMode.Value);
        if (block.ScissorTestEnable.HasValue)
            WriteStateKV(bw, 2, block.ScissorTestEnable.Value ? 1 : 0);
        if (block.MultiSampleAntiAlias.HasValue)
            WriteStateKV(bw, 3, block.MultiSampleAntiAlias.Value ? 1 : 0);
        if (block.DepthBias.HasValue)
            WriteStateKV(bw, 4, BitConverter.SingleToInt32Bits(block.DepthBias.Value));
        if (block.SlopeScaleDepthBias.HasValue)
            WriteStateKV(bw, 5, BitConverter.SingleToInt32Bits(block.SlopeScaleDepthBias.Value));
    }

    private static void WriteStateKV(BinaryWriter bw, byte fieldId, int value)
    {
        bw.Write(fieldId);
        bw.Write(value);
    }

    private static void WriteAnnotations(BinaryWriter bw, IReadOnlyList<AnnotationInfo> annotations)
    {
        bw.Write(annotations.Count);
        foreach (var ann in annotations)
        {
            bw.Write(ann.Name);
            bw.Write(ann.Type);
            switch (ann.Type)
            {
                case TypeSingle:
                    bw.Write(ann.FloatValue.GetValueOrDefault());
                    break;
                case TypeInt32:
                    bw.Write(ann.IntValue.GetValueOrDefault());
                    break;
                case TypeBool:
                    bw.Write(ann.BoolValue.GetValueOrDefault() ? 1 : 0);
                    break;
                default:
                    // String type and any unrecognised type — write as string
                    bw.Write(ann.StringValue ?? "");
                    break;
            }
        }
    }

    private static void WriteInt32List(BinaryWriter bw, IReadOnlyList<int> list)
    {
        bw.Write(list.Count);
        foreach (var v in list)
            bw.Write(v);
    }
}
