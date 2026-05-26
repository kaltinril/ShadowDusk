#nullable enable

using System.IO;
using System.Text;

namespace ShadowDusk.Core;

public sealed class MgfxWriter
{
    private const uint MgfxSignature      = 0x4D474658u;
    private const byte RenderStateSentinel = 0xFF;

    // EffectParameterType byte value for annotation dispatch
    private const byte TypeSingle = 3;
    private const byte TypeInt32  = 2;
    private const byte TypeBool   = 1;
    private const byte TypeString = 4;

    public Result<byte[], ShaderError> Write(ShaderIR ir, MgfxWriterOptions options)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteHeader(bw, options);
        WriteConstantBuffers(bw, ir);
        WriteShaders(bw, ir);
        WriteParameters(bw, ir);
        WriteTechniques(bw, ir);

        bw.Flush();
        return Result<byte[], ShaderError>.Ok(ms.ToArray());
    }

    private static void WriteHeader(BinaryWriter bw, MgfxWriterOptions options)
    {
        bw.Write(MgfxSignature);
        bw.Write(options.MgfxVersion);
        bw.Write((byte)options.Profile);
    }

    private static void WriteConstantBuffers(BinaryWriter bw, ShaderIR ir)
    {
        bw.Write(ir.ConstantBuffers.Count);
        foreach (var cb in ir.ConstantBuffers)
        {
            bw.Write(cb.Name);
            bw.Write((short)cb.SizeInBytes);
            bw.Write(cb.ParameterIndices.Count);
            foreach (var idx in cb.ParameterIndices)
                bw.Write(idx);
            foreach (var offset in cb.ParameterOffsets)
                bw.Write(offset);
        }
    }

    private static void WriteShaders(BinaryWriter bw, ShaderIR ir)
    {
        bw.Write(ir.Shaders.Count);
        foreach (var blob in ir.Shaders)
        {
            bw.Write(blob.Bytes.Length);
            bw.Write(blob.Bytes);
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
                bw.Write((short)pass.VertexShaderIndex);
                bw.Write((short)pass.PixelShaderIndex);
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
