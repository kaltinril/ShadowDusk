#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Byte-format tests for <see cref="KnifxWriter"/> (KNIFX v11). Each test decodes the
/// emitted container the way KNI's runtime reader does (the multi-backend directory header
/// + a packed-int body), so the assertions pin the exact on-disk layout reverse-engineered
/// in <c>plan/PHASE-35-appendix/knifx-format-spec.md</c>.
/// <para>
/// A GL target advertises the whole GL family (OpenGL + GLES + WebGL) so one <c>.knifx</c>
/// loads on every KNI GL host: the OpenGL (desktop) entry carries the faithful version-directory
/// body, while the GLES + WebGL entries share a body whose <c>ShaderVersion</c> is (0,0) so KNI's
/// runtime converts the raw GLSL to the host ES dialect at load (the MGFX-v10 path, proven to load
/// on KNI WebGL). The render proof lives in <c>validation/KniDesktopGL</c> + the KNI WebGL harness;
/// these pin the bytes.
/// </para>
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

    // Parse the backend directory. Returns one (backend, bodyStart) per entry, where bodyStart
    // is the offset of the body's first CONTENT byte (just past its int32 length prefix).
    private static List<(KnifxBackend Backend, int BodyStart)> Directory(byte[] knifx)
    {
        int count = BitConverter.ToInt16(knifx, 8);
        var list = new List<(KnifxBackend, int)>();
        for (int i = 0; i < count; i++)
        {
            int entry = 10 + i * 10;                              // header(10) + entrySize(10)*i
            var backend = (KnifxBackend)BitConverter.ToInt16(knifx, entry);
            int fxOffset = BitConverter.ToInt32(knifx, entry + 6); // backend(2) + effectKey(4)
            list.Add((backend, fxOffset + 4));                    // +4: skip int32 body-length prefix
        }
        return list;
    }

    private static int BodyStart(byte[] knifx, KnifxBackend backend) =>
        Directory(knifx).First(e => e.Backend == backend).BodyStart;

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
    public void Header_VersionIs11_ReservedZero()
    {
        var b = Write(new ShaderIR(), KnifxBackend.DirectX11);
        BitConverter.ToInt16(b, 4).Should().Be(11);  // version
        BitConverter.ToInt16(b, 6).Should().Be(0);   // reserved
    }

    [Fact]
    public void Header_NonGlTarget_IsSingleBackendDirectory()
    {
        var b = Write(MinimalIR(), KnifxBackend.DirectX11);
        BitConverter.ToInt16(b, 8).Should().Be(1);              // backendCount
        BitConverter.ToInt16(b, 10).Should().Be((short)0x0021); // DirectX11
        BitConverter.ToInt32(b, 16).Should().Be(20);            // fxOffset = headerSize 10 + entrySize 10
        int bodyLen = BitConverter.ToInt32(b, 20);              // body length prefix at fxOffset
        b.Length.Should().Be(24 + bodyLen);                     // header(10)+entry(10)+len(4)+body
    }

    [Fact]
    public void Header_GlTarget_AdvertisesWholeGlFamily()
    {
        // A GL target emits a 3-entry directory (OpenGL + GLES + WebGL) so one .knifx loads on
        // KNI desktop GL, mobile GLES, AND the browser. Requesting ANY GL backend yields this.
        foreach (var requested in new[] { KnifxBackend.OpenGL, KnifxBackend.GLES, KnifxBackend.WebGL })
        {
            var b = Write(MinimalIR(), requested);
            BitConverter.ToInt16(b, 8).Should().Be(3);  // backendCount
            Directory(b).Select(e => e.Backend).Should().Equal(
                KnifxBackend.OpenGL, KnifxBackend.GLES, KnifxBackend.WebGL);
        }
    }

    [Fact]
    public void GlTarget_OpenGlHasOwnBody_GlesAndWebGlShareTheRuntimeConvertBody()
    {
        var dir = Directory(Write(MinimalIR(), KnifxBackend.OpenGL));
        int openGl = dir.First(e => e.Backend == KnifxBackend.OpenGL).BodyStart;
        int gles   = dir.First(e => e.Backend == KnifxBackend.GLES).BodyStart;
        int webGl  = dir.First(e => e.Backend == KnifxBackend.WebGL).BodyStart;

        gles.Should().Be(webGl, "GLES and WebGL share the one ShaderVersion(0,0) runtime-convert body");
        openGl.Should().NotBe(webGl, "the desktop OpenGL body is the distinct version-directory body");
    }

    [Fact]
    public void Header_EffectKey_IsFnv1aOfTheBody()
    {
        var b = Write(MinimalIR(), KnifxBackend.DirectX11);
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
        // First body byte (for the requested backend's body) is the integersAsFloats bool.
        (b[BodyStart(b, backend)] != 0).Should().Be(expected);
    }

    // -------------------------------------------------------------------------------------
    // Full structural round-trip of a representative effect (the OpenGL / desktop body)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Body_MinimalEffect_DecodesWithAllNewV11Fields()
    {
        var ir = MinimalIR();
        var b = Write(ir, KnifxBackend.OpenGL);

        using var ms = new MemoryStream(b);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        r.BaseStream.Position = BodyStart(b, KnifxBackend.OpenGL); // the desktop version-directory body

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
        r.BaseStream.Position = BodyStart(b, KnifxBackend.OpenGL);
        r.ReadBoolean();             // integersAsFloats
        ReadPacked(r);               // cbuffer count (0)
        ReadPacked(r).Should().Be(1); // shader count
        r.ReadByte().Should().Be(0);  // Pixel == 0 in KNI
    }

    [Fact]
    public void OpenGlBody_ShaderCode_IsVersionedGlslDirectory_NotRawGlsl()
    {
        // Regression guard for a critical KNIFX correctness gate: with a non-default
        // ShaderVersion, KNI's GL runtime parses ShaderCode as a GLSL-version directory, not
        // raw GLSL. The DESKTOP (OpenGL) body keeps this faithful version-directory form.
        byte[] glsl = Encoding.ASCII.GetBytes("void main(){}");
        var ir = new ShaderIR
        {
            Shaders = new[] { new CompiledShaderBlob(glsl, ShaderStage.Pixel) { ShaderModel = (3, 0) } },
        };
        var b = Write(ir, KnifxBackend.OpenGL);

        byte[] code = ReadShaderCode(b, KnifxBackend.OpenGL);
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
    public void WebGlBody_UsesRawGlsl_WithZeroShaderVersion()
    {
        // The GLES/WebGL body carries ShaderVersion (0,0) + RAW GLSL so KNI's runtime treats it
        // as legacy GLSL and converts it to the host ES dialect at load (the proven MGFX-v10
        // path that makes KNIFX load on KNI WebGL). Contrast OpenGlBody_...VersionedGlslDirectory.
        byte[] glsl = Encoding.ASCII.GetBytes("void main(){}");
        var ir = new ShaderIR
        {
            Shaders = new[] { new CompiledShaderBlob(glsl, ShaderStage.Pixel) { ShaderModel = (3, 0) } },
        };
        var b = Write(ir, KnifxBackend.OpenGL);

        using var r = new BinaryReader(new MemoryStream(b), Encoding.UTF8);
        r.BaseStream.Position = BodyStart(b, KnifxBackend.WebGL);
        r.ReadBoolean();              // integersAsFloats
        ReadPacked(r);                // cbuffer count (0)
        ReadPacked(r).Should().Be(1); // shader count
        r.ReadByte();                 // stage
        ReadPacked(r).Should().Be(0); // ShaderVersion.Major == 0 (raw-GLSL / runtime-convert path)
        ReadPacked(r).Should().Be(0); // ShaderVersion.Minor == 0
        int codeLen = r.ReadInt32();
        r.ReadBytes(codeLen).Should().Equal(glsl, "the web body stores RAW GLSL, not a version directory");
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

        ReadShaderCode(b, KnifxBackend.DirectX11).Should().Equal(dxbc);
    }

    // Navigate a backend's body to its first shader's ShaderCode bytes (0 cbuffers, >=1 shader).
    private static byte[] ReadShaderCode(byte[] knifx, KnifxBackend backend)
    {
        using var r = new BinaryReader(new MemoryStream(knifx), Encoding.UTF8);
        r.BaseStream.Position = BodyStart(knifx, backend);
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
