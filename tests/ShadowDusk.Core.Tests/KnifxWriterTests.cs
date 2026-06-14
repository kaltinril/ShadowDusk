#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Byte-format tests for <see cref="KnifxWriter"/> (KNIFX v11). Each test decodes the
/// emitted container the way KNI's runtime reader does (the multi-backend directory header
/// + a packed-int body), so the assertions pin the exact on-disk layout reverse-engineered
/// in <c>plan/PHASE-35-appendix/knifx-format-spec.md</c>. The render proof (loads + renders
/// in real KNI) lives in the <c>validation/KniDesktopGL</c> rig; these pin the bytes.
/// </summary>
public sealed class KnifxWriterTests
{
    private static byte[] Write(ShaderIR ir, KnifxBackend backend = KnifxBackend.OpenGL)
    {
        var result = new KnifxWriter().Write(ir, new KnifxWriterOptions(backend));
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    // ---- A KNI-faithful packed-int reader (zigzag + 7-bit) -------------------------------
    private static int ReadPacked(BinaryReader r)
    {
        uint zz = 0;
        int shift = 0;
        byte b;
        do
        {
            b = r.ReadByte();
            zz |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return (int)(zz >> 1) ^ -(int)(zz & 1);
    }

    private static int Fnv1a(byte[] data)
    {
        unchecked
        {
            int hash = (int)2166136261;
            const int prime = 16777619;
            foreach (byte b in data)
                hash = (hash ^ b) * prime;
            hash += hash << 13;
            hash ^= hash >> 7;
            hash += hash << 3;
            hash ^= hash >> 17;
            hash += hash << 5;
            return hash;
        }
    }

    // -------------------------------------------------------------------------------------
    // Header (multi-backend directory)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Header_SignatureIsKNIF()
    {
        var b = Write(new ShaderIR());
        Encoding.ASCII.GetString(b, 0, 4).Should().Be("KNIF");
    }

    [Fact]
    public void Header_VersionIs11_ReservedZero_SingleBackendDirectory()
    {
        var b = Write(new ShaderIR());
        BitConverter.ToInt16(b, 4).Should().Be(11);  // version
        BitConverter.ToInt16(b, 6).Should().Be(0);   // reserved
        BitConverter.ToInt16(b, 8).Should().Be(1);   // backendCount (this writer emits one)
    }

    [Theory]
    [InlineData(KnifxBackend.OpenGL, 0x0011)]
    [InlineData(KnifxBackend.GLES, 0x0012)]
    [InlineData(KnifxBackend.WebGL, 0x0014)]
    [InlineData(KnifxBackend.DirectX11, 0x0021)]
    public void Header_DirectoryEntry_BackendValueAndOffsets(KnifxBackend backend, int expected)
    {
        var b = Write(new ShaderIR(), backend);
        BitConverter.ToInt16(b, 10).Should().Be((short)expected);  // backend
        BitConverter.ToInt32(b, 16).Should().Be(20);               // fxOffset = headerSize 10 + entrySize 10
        int bodyLen = BitConverter.ToInt32(b, 20);                 // body length prefix at fxOffset
        b.Length.Should().Be(24 + bodyLen);                        // header(10)+entry(10)+len(4)+body
    }

    [Fact]
    public void Header_EffectKey_IsFnv1aOfTheBody()
    {
        var b = Write(MinimalIR());
        int bodyLen = BitConverter.ToInt32(b, 20);
        byte[] body = b[24..(24 + bodyLen)];
        BitConverter.ToInt32(b, 12).Should().Be(Fnv1a(body),
            "the directory effectKey is FNV-1a/32 (+ avalanche) over the body, per KNI HashHelpers");
    }

    [Theory]
    [InlineData(KnifxBackend.OpenGL, true)]   // OpenGL_Mojo -> integersAsFloats
    [InlineData(KnifxBackend.GLES, true)]
    [InlineData(KnifxBackend.WebGL, true)]
    [InlineData(KnifxBackend.DirectX11, false)]
    public void Body_IntegersAsFloats_FollowsBackend(KnifxBackend backend, bool expected)
    {
        var b = Write(new ShaderIR(), backend);
        // body starts at 24; first body byte is the integersAsFloats bool.
        (b[24] != 0).Should().Be(expected);
    }

    // -------------------------------------------------------------------------------------
    // Full structural round-trip of a representative effect
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Body_MinimalEffect_DecodesWithAllNewV11Fields()
    {
        var ir = MinimalIR();
        var b = Write(ir, KnifxBackend.OpenGL);

        using var ms = new MemoryStream(b);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        r.BaseStream.Position = 24; // skip header(10) + directory entry(10) + body length(4)

        r.ReadBoolean().Should().BeTrue(); // integersAsFloats (OpenGL)

        // ---- constant buffers ----
        ReadPacked(r).Should().Be(1);
        r.ReadString().Should().Be("$Globals");
        ReadPacked(r).Should().Be(64);          // size (packed in v11, was int16 in v10)
        ReadPacked(r).Should().Be(1);           // param-index count
        ReadPacked(r).Should().Be(0);           // param index
        r.ReadUInt16().Should().Be(0);          // offset (still ushort)

        // ---- shaders ----
        ReadPacked(r).Should().Be(1);
        r.ReadByte().Should().Be(1);            // Stage: Vertex == 1 in KNI (Pixel == 0)
        ReadPacked(r).Should().Be(3);           // ShaderVersion.Major  (NEW in v11)
        ReadPacked(r).Should().Be(0);           // ShaderVersion.Minor  (NEW in v11)

        // GL ShaderCode is a GLSL-version bytecode DIRECTORY (NOT raw GLSL): reserved int16,
        // entry count int16, then {byte Major, byte Minor, bool ES, int32 offset}, then the
        // blob {int32 length, bytes}. Verified against KNI ShaderProfileGL.CreateGLSL.
        int codeLen = r.ReadInt32();            // wrapped ShaderCode length
        long codeStart = r.BaseStream.Position;
        r.ReadInt16().Should().Be(0);           // reserved
        r.ReadInt16().Should().Be(1);           // GLSL directory entry count
        r.ReadByte().Should().Be(1);            // GLSL Major (1.10 -> OpenGL desktop entry)
        r.ReadByte().Should().Be(1);            // GLSL Minor
        r.ReadBoolean().Should().BeFalse();     // ES
        r.ReadInt32().Should().Be(11);          // blob offset = HeaderSize 4 + EntrySize 7
        r.ReadInt32().Should().Be(4);           // GLSL blob length
        r.ReadBytes(4).Should().Equal(new byte[] { 1, 2, 3, 4 }); // the GLSL bytes themselves
        (r.BaseStream.Position - codeStart).Should().Be(codeLen, "the directory consumed exactly ShaderCode.Length");

        ReadPacked(r).Should().Be(1);           // sampler count
        r.ReadByte().Should().Be(4);            // sampler type
        r.ReadByte().Should().Be(0);            // textureSlot
        r.ReadByte().Should().Be(0);            // samplerSlot
        r.ReadBoolean().Should().BeFalse();     // hasState
        r.ReadString().Should().Be("vs_s0");    // GL sampler name
        ReadPacked(r).Should().Be(0);           // textureParameter (packed in v11, was byte)
        ReadPacked(r).Should().Be(1);           // cbuffer-index count
        ReadPacked(r).Should().Be(0);           // cbuffer index
        ReadPacked(r).Should().Be(1);           // attribute count
        r.ReadString().Should().Be("vs_v0");    // attribute name
        r.ReadByte().Should().Be(0);            // usage
        ReadPacked(r).Should().Be(0);           // index (packed in v11, was byte)
        r.ReadInt16().Should().Be(0);           // location (int16, unchanged)

        // ---- parameters ----
        ReadPacked(r).Should().Be(1);
        r.ReadByte().Should().Be(2);            // Class = Matrix
        r.ReadByte().Should().Be(3);            // Type
        r.ReadString().Should().Be("WVP");
        r.ReadString().Should().Be("");         // semantic (null -> "")
        ReadPacked(r).Should().Be(0);           // annotation count
        r.ReadByte().Should().Be(4);            // rows
        r.ReadByte().Should().Be(4);            // columns
        r.ReadByte().Should().Be(4);            // columnsActual (NEW in v11) == columns
        ReadPacked(r).Should().Be(0);           // element count
        ReadPacked(r).Should().Be(0);           // member count
        r.ReadBytes(64).Should().OnlyContain(x => x == 0); // value-type leaf default blob (4*4*4)

        // ---- techniques / passes ----
        ReadPacked(r).Should().Be(1);
        r.ReadString().Should().Be("T");
        ReadPacked(r).Should().Be(0);           // technique annotation count
        ReadPacked(r).Should().Be(1);           // pass count
        r.ReadString().Should().Be("P");
        ReadPacked(r).Should().Be(0);           // pass annotation count
        ReadPacked(r).Should().Be(0);           // vertexShaderIndex
        ReadPacked(r).Should().Be(-1);          // pixelShaderIndex (none)
        ReadPacked(r).Should().Be(-1);          // computeShaderIndex (NEW in v11; none)
        r.ReadBoolean().Should().BeFalse();     // blend state present?
        r.ReadBoolean().Should().BeFalse();     // depth-stencil present?
        r.ReadBoolean().Should().BeFalse();     // rasterizer present?

        r.BaseStream.Position.Should().Be(r.BaseStream.Length, "the body decode consumed every byte");
    }

    [Fact]
    public void Body_PixelShaderStageByteIsZero()
    {
        var ir = new ShaderIR
        {
            Shaders = new[]
            {
                new CompiledShaderBlob(new byte[] { 9 }, ShaderStage.Pixel),
            },
        };
        var b = Write(ir);
        using var r = new BinaryReader(new MemoryStream(b), Encoding.UTF8);
        r.BaseStream.Position = 24;
        r.ReadBoolean();             // integersAsFloats
        ReadPacked(r);               // cbuffer count (0)
        ReadPacked(r).Should().Be(1); // shader count
        r.ReadByte().Should().Be(0);  // Pixel == 0 in KNI
    }

    [Fact]
    public void GlShaderCode_IsVersionedGlslDirectory_NotRawGlsl()
    {
        // Regression guard for a critical KNIFX correctness gate: with a non-default
        // ShaderVersion, KNI's GL runtime parses ShaderCode as a GLSL-version directory, not
        // raw GLSL. Emitting raw GLSL here would make KNI throw "Invalid shader bytecode".
        byte[] glsl = Encoding.ASCII.GetBytes("void main(){}");
        var ir = new ShaderIR
        {
            Shaders = new[] { new CompiledShaderBlob(glsl, ShaderStage.Pixel) { ShaderModel = (3, 0) } },
        };
        var b = Write(ir, KnifxBackend.OpenGL);

        byte[] code = ReadFirstShaderCode(b);
        code.Length.Should().BeGreaterThan(glsl.Length, "the GLSL is wrapped in a version directory");
        BitConverter.ToInt16(code, 0).Should().Be(0);   // reserved
        BitConverter.ToInt16(code, 2).Should().Be(1);   // one GLSL version entry
        code[4].Should().Be(1);                          // Major (GLSL 1.10)
        code[5].Should().Be(1);                          // Minor
        code[6].Should().Be(0);                          // ES = false
        BitConverter.ToInt32(code, 7).Should().Be(11);   // blob offset = 4 + 7
        BitConverter.ToInt32(code, 11).Should().Be(glsl.Length); // blob length prefix
        Encoding.ASCII.GetString(code, 15, glsl.Length).Should().Be("void main(){}");
    }

    [Fact]
    public void DxShaderCode_IsRawBytecode_NotWrapped()
    {
        // The GLSL-directory wrapper is GL-only; the DXBC path stores ShaderCode verbatim.
        byte[] dxbc = { 0x44, 0x58, 0x42, 0x43, 1, 2, 3 };
        var ir = new ShaderIR
        {
            Shaders = new[] { new CompiledShaderBlob(dxbc, ShaderStage.Pixel) },
        };
        var b = Write(ir, KnifxBackend.DirectX11);

        ReadFirstShaderCode(b).Should().Equal(dxbc);
    }

    // Navigate the body to the first shader's ShaderCode bytes (0 cbuffers, >=1 shader).
    private static byte[] ReadFirstShaderCode(byte[] knifx)
    {
        using var r = new BinaryReader(new MemoryStream(knifx), Encoding.UTF8);
        r.BaseStream.Position = 24;   // header(10) + directory entry(10) + body length(4)
        r.ReadBoolean();              // integersAsFloats
        ReadPacked(r);                // constant-buffer count
        ReadPacked(r);                // shader count
        r.ReadByte();                 // stage
        ReadPacked(r);                // ShaderVersion.Major
        ReadPacked(r);                // ShaderVersion.Minor
        int codeLen = r.ReadInt32();
        return r.ReadBytes(codeLen);
    }

    // A representative single-VS, single-param effect exercising every new v11 field.
    private static ShaderIR MinimalIR() => new()
    {
        ConstantBuffers = new[]
        {
            new ConstantBufferInfo("$Globals", 64, new[] { 0 }, new ushort[] { 0 }),
        },
        Shaders = new[]
        {
            new CompiledShaderBlob(new byte[] { 1, 2, 3, 4 }, ShaderStage.Vertex)
            {
                ShaderModel = (3, 0),
                Samplers = new[] { new MgfxSamplerInfo(4, 0, 0, "vs_s0", 0) },
                ConstantBufferIndices = new[] { 0 },
                Attributes = new[] { new MgfxVertexAttributeInfo("vs_v0", 0, 0, 0) },
            },
        },
        Parameters = new[]
        {
            new EffectParameterInfo(
                Class: 2, Type: 3, Name: "WVP", Semantic: null,
                Annotations: Array.Empty<AnnotationInfo>(),
                RowCount: 4, ColumnCount: 4,
                Members: Array.Empty<EffectParameterInfo>(),
                Elements: Array.Empty<EffectParameterInfo>()),
        },
        Techniques = new[]
        {
            new MgfxTechniqueInfo("T", Array.Empty<AnnotationInfo>(), new[]
            {
                new MgfxPassInfo("P", Array.Empty<AnnotationInfo>(), 0, -1, new RenderStateBlock()),
            }),
        },
    };
}
