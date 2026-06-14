#nullable enable

using System.IO;
using System.Text;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

public sealed class MgfxWriterTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly MgfxWriterOptions DefaultOptions =
        new(MgfxProfile.OpenGL);

    private static byte[] Write(ShaderIR ir, MgfxWriterOptions? options = null)
    {
        var writer = new MgfxWriter();
        var result = writer.Write(ir, options ?? DefaultOptions);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    private static BinaryReader ReaderFor(byte[] bytes)
        => new(new MemoryStream(bytes), Encoding.UTF8, leaveOpen: false);

    private static ShaderIR EmptyIR() => new();

    private static EffectParameterInfo MakeParameter(
        string name, string? semantic = null,
        IReadOnlyList<AnnotationInfo>? annotations = null)
        => new(
            Class: 0,
            Type: 0,
            Name: name,
            Semantic: semantic,
            Annotations: annotations ?? Array.Empty<AnnotationInfo>(),
            RowCount: 0,
            ColumnCount: 0,
            Members: Array.Empty<EffectParameterInfo>(),
            Elements: Array.Empty<EffectParameterInfo>());

    private static MgfxPassInfo EmptyPass(
        string name = "Pass0",
        int vsIndex = -1,
        int psIndex = -1,
        RenderStateBlock? renderState = null)
        => new(
            Name: name,
            Annotations: Array.Empty<AnnotationInfo>(),
            VertexShaderIndex: vsIndex,
            PixelShaderIndex: psIndex,
            RenderState: renderState ?? new RenderStateBlock());

    private static MgfxTechniqueInfo MakeTechnique(
        string name, params MgfxPassInfo[] passes)
        => new(
            Name: name,
            Annotations: Array.Empty<AnnotationInfo>(),
            Passes: passes);

    // -------------------------------------------------------------------------
    // 9.1 Header tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Header_SignatureIsCorrect()
    {
        var bytes = Write(EmptyIR());

        // Forward "MGFX" — the exact byte sequence MonoGame's EffectReader requires.
        bytes[0].Should().Be(0x4D); // 'M'
        bytes[1].Should().Be(0x47); // 'G'
        bytes[2].Should().Be(0x46); // 'F'
        bytes[3].Should().Be(0x58); // 'X'
    }

    [Fact]
    public void Header_DefaultVersionIs10()
    {
        var bytes = Write(EmptyIR());
        bytes[4].Should().Be(10);
    }

    [Fact]
    public void Header_Version11WhenRequested()
    {
        var bytes = Write(EmptyIR(), new MgfxWriterOptions(MgfxProfile.OpenGL, MgfxVersion: 11));
        bytes[4].Should().Be(11);
    }

    // -------------------------------------------------------------------------
    // MGFX v11 per-shader SourceFile + Entrypoint (MonoGame PR #8813). v11 writes
    // them after the isVertexShader bool and before the bytecode length; v10 omits
    // them. Header is 10 bytes (MGFX[4] + version[1] + profile[1] + effectKey[4]);
    // body starts with the cbuffer count then the shader count.
    // -------------------------------------------------------------------------

    private static ShaderIR OneShaderIR(string sourceFile, string entrypoint) => new()
    {
        Shaders = new[]
        {
            new CompiledShaderBlob(new byte[] { 1, 2, 3 }, ShaderStage.Pixel)
            {
                SourceFile = sourceFile,
                Entrypoint = entrypoint,
            },
        },
    };

    [Fact]
    public void Shaders_V10_OmitsSourceFileAndEntrypoint()
    {
        var bytes = Write(OneShaderIR("foo.fx", "PSMain"),
            new MgfxWriterOptions(MgfxProfile.OpenGL, MgfxVersion: 10));
        using var r = ReaderFor(bytes);
        r.BaseStream.Position = 10;       // skip header
        r.ReadInt32().Should().Be(0);     // constant-buffer count
        r.ReadInt32().Should().Be(1);     // shader count
        r.ReadBoolean();                  // isVertexShader
        r.ReadInt32().Should().Be(3);     // v10: bytecode length immediately (no strings)
    }

    [Fact]
    public void Shaders_V11_WritesSourceFileThenEntrypointBeforeBytecode()
    {
        var bytes = Write(OneShaderIR("foo.fx", "PSMain"),
            new MgfxWriterOptions(MgfxProfile.OpenGL, MgfxVersion: 11));
        using var r = ReaderFor(bytes);
        r.BaseStream.Position = 10;
        r.ReadInt32().Should().Be(0);          // constant-buffer count
        r.ReadInt32().Should().Be(1);          // shader count
        r.ReadBoolean();                       // isVertexShader
        r.ReadString().Should().Be("foo.fx");  // v11: SourceFile
        r.ReadString().Should().Be("PSMain");  // v11: Entrypoint
        r.ReadInt32().Should().Be(3);          // then bytecode length
    }

    [Fact]
    public void Shaders_V11_DefaultsToUnknownWhenUnset()
    {
        var bytes = Write(new ShaderIR { Shaders = new[] { new CompiledShaderBlob(new byte[] { 9 }, ShaderStage.Vertex) } },
            new MgfxWriterOptions(MgfxProfile.OpenGL, MgfxVersion: 11));
        using var r = ReaderFor(bytes);
        r.BaseStream.Position = 10;
        r.ReadInt32();                          // cbuffer count
        r.ReadInt32();                          // shader count
        r.ReadBoolean();                        // isVertexShader
        r.ReadString().Should().Be("<unknown>");  // SourceFile default (mgfxc's own fallback)
        r.ReadString().Should().Be("<unknown>");  // Entrypoint default
    }

    [Fact]
    public void Header_OpenGlProfileId()
    {
        var bytes = Write(EmptyIR(), new MgfxWriterOptions(MgfxProfile.OpenGL));
        bytes[5].Should().Be(0);
    }

    [Fact]
    public void Header_DirectX11ProfileId()
    {
        var bytes = Write(EmptyIR(), new MgfxWriterOptions(MgfxProfile.DirectX11));
        bytes[5].Should().Be(1);
    }

    [Fact]
    public void Header_VulkanProfileId()
    {
        var bytes = Write(EmptyIR(), new MgfxWriterOptions(MgfxProfile.Vulkan));
        bytes[5].Should().Be(3);
    }

    // -------------------------------------------------------------------------
    // 9.2 String encoding tests
    // -------------------------------------------------------------------------

    [Fact]
    public void StringEncoding_EmptyString()
    {
        var ir = new ShaderIR
        {
            Parameters = [MakeParameter("")],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        var count = br.ReadInt32();
        count.Should().Be(1);

        br.ReadByte(); // Class
        br.ReadByte(); // Type
        var name = br.ReadString();
        name.Should().BeEmpty();
    }

    [Fact]
    public void StringEncoding_ShortString()
    {
        var ir = new ShaderIR
        {
            Parameters = [MakeParameter("World")],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        br.ReadInt32(); // count
        br.ReadByte(); // Class
        br.ReadByte(); // Type

        // BinaryWriter encodes "World" (5 chars) as single length byte 0x05
        // Peek at the raw stream position to verify the prefix byte
        var ms = (MemoryStream)br.BaseStream;
        var prefixByte = bytes[ms.Position];
        prefixByte.Should().Be(0x05, because: "5 chars fits in a single 7-bit length byte");

        var name = br.ReadString();
        name.Should().Be("World");
    }

    [Fact]
    public void StringEncoding_LongString()
    {
        var longName = new string('A', 200);
        var ir = new ShaderIR
        {
            Parameters = [MakeParameter(longName)],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        br.ReadInt32(); // count
        br.ReadByte(); // Class
        br.ReadByte(); // Type

        // 200 chars > 127 so the 7-bit encoding uses two bytes; first byte has high bit set
        var ms = (MemoryStream)br.BaseStream;
        var prefixByte = bytes[ms.Position];
        (prefixByte & 0x80).Should().NotBe(0, because: "200-char string needs a two-byte 7-bit encoded prefix");

        var name = br.ReadString();
        name.Should().Be(longName);
    }

    [Fact]
    public void StringEncoding_Roundtrip()
    {
        var names = Enumerable.Range(0, 20).Select(i => $"Parameter{i:D3}").ToArray();
        var ir = new ShaderIR
        {
            Parameters = names.Select(n => MakeParameter(n)).ToArray(),
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        var count = br.ReadInt32();
        count.Should().Be(20);
        for (var i = 0; i < 20; i++)
        {
            br.ReadByte(); // Class
            br.ReadByte(); // Type
            br.ReadString().Should().Be(names[i]);
            br.ReadString(); // semantic
            var annCount = br.ReadInt32();
            annCount.Should().Be(0);
            br.ReadByte(); // RowCount
            br.ReadByte(); // ColumnCount
            var memberCount = br.ReadInt32();
            memberCount.Should().Be(0);
            var elemCount = br.ReadInt32();
            elemCount.Should().Be(0);
        }
    }

    [Fact]
    public void StringEncoding_SemanticAbsent()
    {
        var ir = new ShaderIR
        {
            Parameters = [MakeParameter("Tex", semantic: null)],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        br.ReadInt32(); // count
        br.ReadByte(); // Class
        br.ReadByte(); // Type
        br.ReadString(); // name
        var semantic = br.ReadString();
        semantic.Should().BeEmpty(because: "null semantic must be written as empty string");
    }

    // -------------------------------------------------------------------------
    // 9.3 Technique ordering tests
    // -------------------------------------------------------------------------

    [Fact]
    public void TechniqueOrder_PreservedFromIR()
    {
        var ir = new ShaderIR
        {
            Techniques =
            [
                MakeTechnique("C", EmptyPass()),
                MakeTechnique("A", EmptyPass()),
                MakeTechnique("B", EmptyPass()),
            ],
        };

        var bytes = Write(ir);
        var names = ReadTechniqueNames(bytes);
        names.Should().ContainInOrder("C", "A", "B");
    }

    [Fact]
    public void TechniqueOrder_SingleTechnique()
    {
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("Only", EmptyPass())],
        };

        var bytes = Write(ir);
        var names = ReadTechniqueNames(bytes);
        names.Should().ContainSingle().Which.Should().Be("Only");
    }

    [Fact]
    public void TechniqueOrder_PassCountPerTechnique()
    {
        var ir = new ShaderIR
        {
            Techniques =
            [
                MakeTechnique("T1", EmptyPass("P1"), EmptyPass("P2"), EmptyPass("P3")),
                MakeTechnique("T2", EmptyPass("P1")),
            ],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);
        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);
        SkipParameters(br);

        var techCount = br.ReadInt32();
        techCount.Should().Be(2);

        br.ReadString(); // T1 name
        ReadAnnotationList(br); // T1 annotations
        var passCount1 = br.ReadInt32();
        passCount1.Should().Be(3);
        for (var i = 0; i < 3; i++) SkipPass(br);

        br.ReadString(); // T2 name
        ReadAnnotationList(br); // T2 annotations
        var passCount2 = br.ReadInt32();
        passCount2.Should().Be(1);
    }

    // -------------------------------------------------------------------------
    // 9.4 Shader blob tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ShaderBlob_LengthPrefixCorrect()
    {
        var blobBytes = new byte[64];
        var ir = new ShaderIR
        {
            Shaders = [new CompiledShaderBlob(blobBytes, ShaderStage.Vertex)],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);

        var count = br.ReadInt32();
        count.Should().Be(1);
        br.ReadBoolean(); // isVertexShader
        var length = br.ReadInt32();
        length.Should().Be(64);
        var blob = br.ReadBytes(64);
        blob.Should().HaveCount(64);
    }

    [Fact]
    public void ShaderBlob_ZeroBlobs()
    {
        var bytes = Write(EmptyIR());
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);

        var count = br.ReadInt32();
        count.Should().Be(0);
    }

    [Fact]
    public void ShaderBlob_MultipleBlobs()
    {
        var b1 = new byte[8];
        var b2 = new byte[16];
        var b3 = new byte[32];
        b1[0] = 0xAA; b2[0] = 0xBB; b3[0] = 0xCC;

        var ir = new ShaderIR
        {
            Shaders =
            [
                new CompiledShaderBlob(b1, ShaderStage.Vertex),
                new CompiledShaderBlob(b2, ShaderStage.Pixel),
                new CompiledShaderBlob(b3, ShaderStage.Vertex),
            ],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);

        var count = br.ReadInt32();
        count.Should().Be(3);

        static (int Len, byte First) ReadShaderRecord(BinaryReader r)
        {
            r.ReadBoolean();                 // isVertexShader
            int len = r.ReadInt32();
            byte first = r.ReadBytes(len)[0];
            r.ReadByte();                    // samplerCount (0)
            r.ReadByte();                    // cbufferIndexCount (0)
            r.ReadByte();                    // attributeCount (0)
            return (len, first);
        }

        var (len1, first1) = ReadShaderRecord(br); len1.Should().Be(8);  first1.Should().Be(0xAA);
        var (len2, first2) = ReadShaderRecord(br); len2.Should().Be(16); first2.Should().Be(0xBB);
        var (len3, first3) = ReadShaderRecord(br); len3.Should().Be(32); first3.Should().Be(0xCC);
    }

    // -------------------------------------------------------------------------
    // 9.5 Constant buffer tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ConstantBuffer_ZeroBuffers()
    {
        var bytes = Write(EmptyIR());
        using var br = ReaderFor(bytes);

        SkipHeader(br);

        var count = br.ReadInt32();
        count.Should().Be(0);
    }

    [Fact]
    public void ConstantBuffer_SingleBuffer()
    {
        var ir = new ShaderIR
        {
            ConstantBuffers =
            [
                new ConstantBufferInfo(
                    Name: "Globals",
                    SizeInBytes: 64,
                    ParameterIndices: new[] { 0, 1 },
                    ParameterOffsets: new ushort[] { 0, 16 })
            ],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);

        var count = br.ReadInt32();
        count.Should().Be(1);

        var name = br.ReadString();
        name.Should().Be("Globals");

        var size = br.ReadInt16();
        size.Should().Be(64);

        var paramCount = br.ReadInt32();
        paramCount.Should().Be(2);

        // Interleaved: index(int32) then offset(uint16) per parameter.
        br.ReadInt32().Should().Be(0);
        br.ReadUInt16().Should().Be(0);
        br.ReadInt32().Should().Be(1);
        br.ReadUInt16().Should().Be(16);
    }

    [Fact]
    public void ConstantBuffer_ParameterOffsets_AreUInt16()
    {
        var ir = new ShaderIR
        {
            ConstantBuffers =
            [
                new ConstantBufferInfo(
                    Name: "BigCB",
                    SizeInBytes: 256,
                    ParameterIndices: new[] { 0 },
                    ParameterOffsets: new ushort[] { 65000 })
            ],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);

        br.ReadInt32(); // count
        br.ReadString(); // name
        br.ReadInt16();  // size
        br.ReadInt32();  // paramCount
        br.ReadInt32();  // index 0

        var offset = br.ReadUInt16();
        offset.Should().Be(65000, because: "uint16 can represent values up to 65535");
    }

    [Fact]
    public void ConstantBuffer_SizeExceedsInt16_ReturnsError()
    {
        var ir = new ShaderIR
        {
            ConstantBuffers =
            [
                new ConstantBufferInfo(
                    Name: "TooBig",
                    SizeInBytes: short.MaxValue + 1,
                    ParameterIndices: [],
                    ParameterOffsets: [])
            ],
        };

        var result = new MgfxWriter().Write(ir, new MgfxWriterOptions(MgfxProfile.OpenGL));
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0020");
    }

    [Fact]
    public void ShaderIndex_ExceedsInt16_ReturnsError()
    {
        var ir = new ShaderIR
        {
            Techniques =
            [
                new MgfxTechniqueInfo("T", [], [
                    new MgfxPassInfo("P", [], VertexShaderIndex: short.MaxValue + 1, PixelShaderIndex: -1, new RenderStateBlock())
                ])
            ],
        };

        var result = new MgfxWriter().Write(ir, new MgfxWriterOptions(MgfxProfile.OpenGL));
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0021");
    }

    [Fact]
    public void Shader_ConstantBufferIndexExceedsByte_ReturnsError()
    {
        // The cbuffer-index list is serialized as single bytes — an index > 255 must
        // fail loudly (SD0022) instead of silently truncating into a corrupt .mgfx.
        var ir = new ShaderIR
        {
            Shaders =
            [
                new CompiledShaderBlob([1, 2, 3], ShaderStage.Pixel)
                {
                    ConstantBufferIndices = [byte.MaxValue + 1],
                },
            ],
        };

        var result = new MgfxWriter().Write(ir, new MgfxWriterOptions(MgfxProfile.OpenGL));
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0022");
    }

    [Fact]
    public void Shader_SamplerParameterIndexExceedsByte_ReturnsError()
    {
        var ir = new ShaderIR
        {
            Shaders =
            [
                new CompiledShaderBlob([1, 2, 3], ShaderStage.Pixel)
                {
                    Samplers =
                    [
                        new MgfxSamplerInfo(
                            Type: 0,
                            TextureSlot: 0,
                            SamplerSlot: 0,
                            Name: "ps_s0",
                            Parameter: byte.MaxValue + 1),
                    ],
                },
            ],
        };

        var result = new MgfxWriter().Write(ir, new MgfxWriterOptions(MgfxProfile.OpenGL));
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0022");
    }

    [Fact]
    public void Shader_ByteRangeValues_AtTheLimit_Succeed()
    {
        // 255 is representable — only 256+ trips the guard.
        var ir = new ShaderIR
        {
            Shaders =
            [
                new CompiledShaderBlob([1, 2, 3], ShaderStage.Pixel)
                {
                    ConstantBufferIndices = [byte.MaxValue],
                },
            ],
        };

        var result = new MgfxWriter().Write(ir, new MgfxWriterOptions(MgfxProfile.OpenGL));
        result.IsSuccess.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // 9.6 Render state tests
    // -------------------------------------------------------------------------

    [Fact]
    public void RenderState_NotPresent_WritesFlagZero()
    {
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass())],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);
        SkipParameters(br);

        br.ReadInt32(); // technique count
        br.ReadString(); // technique name
        ReadAnnotationList(br);
        br.ReadInt32(); // pass count
        br.ReadString(); // pass name
        ReadAnnotationList(br);
        br.ReadInt32(); // vs index
        br.ReadInt32(); // ps index

        // Three presence bytes should all be zero
        br.ReadByte().Should().Be(0, because: "blend state not specified");
        br.ReadByte().Should().Be(0, because: "depth stencil state not specified");
        br.ReadByte().Should().Be(0, because: "rasterizer state not specified");
    }

    // The fixed field layouts below mirror MonoGame 3.8.2 Effect.ReadPasses
    // verbatim (Phase 43, F1): a bool presence flag, then every field of the
    // state object in alphabetical order — no field IDs, no terminator.

    /// <summary>Reads the 16-byte fixed blend block exactly as MonoGame 3.8.2 does.</summary>
    private static (byte AlphaFunc, byte AlphaDst, byte AlphaSrc, byte[] BlendFactor,
                    byte ColorFunc, byte ColorDst, byte ColorSrc,
                    byte Cwc0, byte Cwc1, byte Cwc2, byte Cwc3, int MultiSampleMask)
        ReadBlendBlock(BinaryReader br) => (
            br.ReadByte(), br.ReadByte(), br.ReadByte(),
            br.ReadBytes(4),
            br.ReadByte(), br.ReadByte(), br.ReadByte(),
            br.ReadByte(), br.ReadByte(), br.ReadByte(), br.ReadByte(),
            br.ReadInt32());

    [Fact]
    public void RenderState_CullModeNone()
    {
        var renderState = new RenderStateBlock { CullMode = CullModeValue.None };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeFalse(because: "no blend state");
        br.ReadBoolean().Should().BeFalse(because: "no depth stencil state");
        br.ReadBoolean().Should().BeTrue(because: "rasterizer state present");

        // MonoGame reads: CullMode, DepthBias, FillMode, MultiSampleAntiAlias,
        // ScissorTestEnable, SlopeScaleDepthBias.
        br.ReadByte().Should().Be((byte)CullModeValue.None, because: "MonoGame CullMode.None == 0");
        br.ReadSingle().Should().Be(0f, because: "DepthBias defaults to 0 (RasterizerState ctor)");
        br.ReadByte().Should().Be((byte)FillModeValue.Solid);
        br.ReadBoolean().Should().BeTrue(because: "MultiSampleAntiAlias defaults to true");
        br.ReadBoolean().Should().BeFalse(because: "ScissorTestEnable defaults to false");
        br.ReadSingle().Should().Be(0f);
    }

    [Fact]
    public void RenderState_AlphaBlend_SourceDestFactors()
    {
        var renderState = new RenderStateBlock
        {
            ColorSourceBlend      = BlendValue.SourceAlpha,
            ColorDestinationBlend = BlendValue.InverseSourceAlpha,
        };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeTrue(because: "blend state present");
        var blend = ReadBlendBlock(br);

        blend.ColorSrc.Should().Be((byte)BlendValue.SourceAlpha);
        blend.ColorDst.Should().Be((byte)BlendValue.InverseSourceAlpha);
        // mgfxc derives the alpha channel from SrcBlend/DestBlend via ToAlphaBlend
        // (identity for the alpha-form blends used here).
        blend.AlphaSrc.Should().Be((byte)BlendValue.SourceAlpha);
        blend.AlphaDst.Should().Be((byte)BlendValue.InverseSourceAlpha);
        blend.ColorFunc.Should().Be((byte)BlendFunctionValue.Add);
        blend.AlphaFunc.Should().Be((byte)BlendFunctionValue.Add);
        blend.BlendFactor.Should().Equal(255, 255, 255, 255); // Color.White default
        blend.Cwc0.Should().Be(15); blend.Cwc1.Should().Be(15);
        blend.Cwc2.Should().Be(15); blend.Cwc3.Should().Be(15);
        blend.MultiSampleMask.Should().Be(int.MaxValue);

        br.ReadBoolean().Should().BeFalse(because: "no depth stencil state");
        br.ReadBoolean().Should().BeFalse(because: "no rasterizer state");
    }

    [Fact]
    public void RenderState_AlphaBlendEnable_True_PresetsPremultiplied()
    {
        // mgfxc PassInfo: AlphaBlendEnable=TRUE alone presets One/InverseSourceAlpha.
        var renderState = new RenderStateBlock { AlphaBlendEnable = true };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeTrue();
        var blend = ReadBlendBlock(br);
        blend.ColorSrc.Should().Be((byte)BlendValue.One);
        blend.AlphaSrc.Should().Be((byte)BlendValue.One);
        blend.ColorDst.Should().Be((byte)BlendValue.InverseSourceAlpha);
        blend.AlphaDst.Should().Be((byte)BlendValue.InverseSourceAlpha);
    }

    [Fact]
    public void RenderState_BlendOp_LandsInAlphaBlendFunction()
    {
        // mgfxc quirk mirrored for fidelity: BlendOp assigns ONLY AlphaBlendFunction;
        // ColorBlendFunction always ships as Add.
        var renderState = new RenderStateBlock { ColorBlendFunction = BlendFunctionValue.ReverseSubtract };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeTrue();
        var blend = ReadBlendBlock(br);
        blend.AlphaFunc.Should().Be((byte)BlendFunctionValue.ReverseSubtract);
        blend.ColorFunc.Should().Be((byte)BlendFunctionValue.Add);
    }

    [Fact]
    public void RenderState_DepthBufferEnable()
    {
        var renderState = new RenderStateBlock { DepthBufferEnable = false };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeFalse(); // blend not present
        br.ReadBoolean().Should().BeTrue();  // depth-stencil present

        // MonoGame reads: CCWStencilDepthBufferFail, CCWStencilFail, CCWStencilFunction,
        // CCWStencilPass, DepthBufferEnable, DepthBufferFunction, DepthBufferWriteEnable,
        // ReferenceStencil, StencilDepthBufferFail, StencilEnable, StencilFail,
        // StencilFunction, StencilMask, StencilPass, StencilWriteMask, TwoSidedStencilMode.
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadByte().Should().Be((byte)CompareFunctionValue.Always);
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadBoolean().Should().BeFalse(because: "DepthBufferEnable was set to false");
        br.ReadByte().Should().Be((byte)CompareFunctionValue.LessEqual, because: "DepthStencilState ctor default");
        br.ReadBoolean().Should().BeTrue(because: "DepthBufferWriteEnable defaults to true");
        br.ReadInt32().Should().Be(0, because: "ReferenceStencil defaults to 0");
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadBoolean().Should().BeFalse(because: "StencilEnable defaults to false");
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadByte().Should().Be((byte)CompareFunctionValue.Always);
        br.ReadInt32().Should().Be(int.MaxValue, because: "StencilMask defaults to Int32.MaxValue");
        br.ReadByte().Should().Be((byte)StencilOperationValue.Keep);
        br.ReadInt32().Should().Be(int.MaxValue, because: "StencilWriteMask defaults to Int32.MaxValue");
        br.ReadBoolean().Should().BeFalse(because: "TwoSidedStencilMode defaults to false");

        br.ReadBoolean().Should().BeFalse(); // rasterizer not present
    }

    [Fact]
    public void RenderState_DepthFunction_Greater()
    {
        var renderState = new RenderStateBlock { DepthBufferFunction = CompareFunctionValue.Greater };
        var ir = new ShaderIR
        {
            Techniques = [MakeTechnique("T", EmptyPass(renderState: renderState))],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipToFirstPassRenderState(br);

        br.ReadBoolean().Should().BeFalse(); // blend not present
        br.ReadBoolean().Should().BeTrue();  // depth-stencil present

        br.ReadBytes(4);                     // CCW stencil quad
        br.ReadBoolean().Should().BeTrue(because: "DepthBufferEnable defaults to true");
        br.ReadByte().Should().Be((byte)CompareFunctionValue.Greater);
    }

    // -------------------------------------------------------------------------
    // 9.6b Annotations (Phase 43, F2): count only, NO bodies
    // -------------------------------------------------------------------------

    [Fact]
    public void Annotations_WriteCountOnly_NoBodies()
    {
        var annotations = new List<AnnotationInfo>
        {
            new(Name: "UIName",  Type: 4, StringValue: "Tint Color", FloatValue: null, IntValue: null, BoolValue: null),
            new(Name: "UIOrder", Type: 2, StringValue: null, FloatValue: null, IntValue: 1, BoolValue: null),
        };
        var ir = new ShaderIR
        {
            Parameters = [MakeParameter("TintColor", annotations: annotations)],
            Techniques = [MakeTechnique("T", EmptyPass())],
        };

        var bytes = Write(ir);
        using var br = ReaderFor(bytes);

        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);

        br.ReadInt32().Should().Be(1);          // parameter count
        br.ReadByte(); br.ReadByte();           // class, type
        br.ReadString().Should().Be("TintColor");
        br.ReadString();                        // semantic
        br.ReadInt32().Should().Be(2, because: "the annotation COUNT is preserved");

        // MonoGame 3.8.2 ReadAnnotations reads ONLY the count — the very next bytes
        // must be the parameter's RowCount/ColumnCount, not annotation bodies.
        br.ReadByte().Should().Be(0, because: "RowCount follows the count immediately");
        br.ReadByte().Should().Be(0, because: "ColumnCount follows");
        br.ReadInt32().Should().Be(0);          // member indices
        br.ReadInt32().Should().Be(0);          // element indices
    }

    // -------------------------------------------------------------------------
    // 9.7 Full minimal effect round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void MinimalEffect_SingleTechniqueSinglePass()
    {
        var vsBytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var psBytes = new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 };

        var ir = new ShaderIR
        {
            ConstantBuffers = Array.Empty<ConstantBufferInfo>(),
            Shaders =
            [
                new CompiledShaderBlob(vsBytes, ShaderStage.Vertex),
                new CompiledShaderBlob(psBytes, ShaderStage.Pixel),
            ],
            Parameters = [MakeParameter("Texture0", semantic: null)],
            Techniques =
            [
                MakeTechnique("Technique0",
                    new MgfxPassInfo(
                        Name: "Pass0",
                        Annotations: Array.Empty<AnnotationInfo>(),
                        VertexShaderIndex: 0,
                        PixelShaderIndex: 1,
                        RenderState: new RenderStateBlock())),
            ],
        };

        var bytes = Write(ir, new MgfxWriterOptions(MgfxProfile.DirectX11));

        using var br = ReaderFor(bytes);

        // Header — forward "MGFX" reads little-endian as 0x5846474D
        br.ReadUInt32().Should().Be(0x5846474Du);
        br.ReadByte().Should().Be(10); // version
        br.ReadByte().Should().Be(1);  // DirectX11 profile
        br.ReadInt32();                // EffectKey (content-derived)

        // Constant buffers: count = 0
        br.ReadInt32().Should().Be(0);

        // Shaders: count = 2 (full per-shader record: stage flag + blob + empty tables)
        br.ReadInt32().Should().Be(2);
        br.ReadBoolean().Should().BeTrue();  // VS isVertexShader
        br.ReadInt32().Should().Be(8);       // VS blob length
        br.ReadBytes(8).Should().Equal(vsBytes);
        br.ReadByte().Should().Be(0); br.ReadByte().Should().Be(0); br.ReadByte().Should().Be(0);
        br.ReadBoolean().Should().BeFalse(); // PS isVertexShader
        br.ReadInt32().Should().Be(8);       // PS blob length
        br.ReadBytes(8).Should().Equal(psBytes);
        br.ReadByte().Should().Be(0); br.ReadByte().Should().Be(0); br.ReadByte().Should().Be(0);

        // Parameters: count = 1
        br.ReadInt32().Should().Be(1);
        br.ReadByte().Should().Be(0);  // Class
        br.ReadByte().Should().Be(0);  // Type
        br.ReadString().Should().Be("Texture0");
        br.ReadString().Should().BeEmpty(); // semantic
        br.ReadInt32().Should().Be(0); // annotations count
        br.ReadByte().Should().Be(0);  // RowCount
        br.ReadByte().Should().Be(0);  // ColumnCount
        br.ReadInt32().Should().Be(0); // member count
        br.ReadInt32().Should().Be(0); // element count
        // (Class 0 / 0x0 rows*cols => zero-length default-value blob)

        // Techniques: count = 1
        br.ReadInt32().Should().Be(1);
        br.ReadString().Should().Be("Technique0");
        br.ReadInt32().Should().Be(0); // technique annotations count
        br.ReadInt32().Should().Be(1); // pass count

        // Pass
        br.ReadString().Should().Be("Pass0");
        br.ReadInt32().Should().Be(0); // pass annotations count
        br.ReadInt32().Should().Be(0); // VS index (int32)
        br.ReadInt32().Should().Be(1); // PS index (int32)

        // Render state: all three blocks absent
        br.ReadByte().Should().Be(0);
        br.ReadByte().Should().Be(0);
        br.ReadByte().Should().Be(0);

        // Footer: trailing "MGFX"
        br.ReadUInt32().Should().Be(0x5846474Du);
    }

    // -------------------------------------------------------------------------
    // Skip / read helpers
    // -------------------------------------------------------------------------

    private static void SkipHeader(BinaryReader br)
    {
        br.ReadUInt32(); // signature
        br.ReadByte();   // version
        br.ReadByte();   // profile
        br.ReadInt32();  // EffectKey
    }

    private static void SkipConstantBuffers(BinaryReader br)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            br.ReadString(); // name
            br.ReadInt16();  // size
            var paramCount = br.ReadInt32();
            for (var j = 0; j < paramCount; j++) { br.ReadInt32(); br.ReadUInt16(); } // interleaved index+offset
        }
    }

    private static void SkipShaders(BinaryReader br)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            br.ReadBoolean();          // isVertexShader
            var len = br.ReadInt32();
            br.ReadBytes(len);

            int samplerCount = br.ReadByte();
            for (var s = 0; s < samplerCount; s++)
            {
                br.ReadByte();         // type
                br.ReadByte();         // textureSlot
                br.ReadByte();         // samplerSlot
                if (br.ReadBoolean())  // hasState
                {
                    br.ReadBytes(3); br.ReadBytes(4); br.ReadByte();
                    br.ReadInt32(); br.ReadInt32(); br.ReadSingle();
                }
                br.ReadString();       // name
                br.ReadByte();         // parameter index
            }

            int cbIndexCount = br.ReadByte();
            for (var c = 0; c < cbIndexCount; c++) br.ReadByte();

            int attrCount = br.ReadByte();
            for (var a = 0; a < attrCount; a++)
            {
                br.ReadString(); br.ReadByte(); br.ReadByte(); br.ReadInt16();
            }
        }
    }

    private static void SkipParameters(BinaryReader br)
    {
        var count = br.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            byte paramClass = br.ReadByte(); // Class
            br.ReadByte(); // Type
            br.ReadString(); // name
            br.ReadString(); // semantic
            ReadAnnotationList(br);
            byte rowCount    = br.ReadByte(); // RowCount
            byte columnCount = br.ReadByte(); // ColumnCount
            var memberCount = br.ReadInt32();
            for (var j = 0; j < memberCount; j++) br.ReadInt32();
            var elemCount = br.ReadInt32();
            for (var j = 0; j < elemCount; j++) br.ReadInt32();
            if (paramClass <= 2 && memberCount == 0 && elemCount == 0)
                br.ReadBytes(rowCount * columnCount * 4); // default-value blob
        }
    }

    // MGFX v10 annotations are the int32 count and nothing else — MonoGame's
    // ReadAnnotations never reads bodies (Phase 43, F2).
    private static void ReadAnnotationList(BinaryReader br) => br.ReadInt32();

    private static void SkipPass(BinaryReader br)
    {
        br.ReadString(); // name
        ReadAnnotationList(br);
        br.ReadInt32(); // vs index
        br.ReadInt32(); // ps index
        SkipRenderStateBlock(br);
    }

    private static void SkipRenderStateBlock(BinaryReader br)
    {
        // MonoGame 3.8.2 fixed layouts: blend 14 bytes + int32; depth-stencil
        // 8 enum/bool bytes + 3 bool bytes + 3 int32; rasterizer 2 enum bytes +
        // 2 bool bytes + 2 singles.
        if (br.ReadBoolean()) { br.ReadBytes(14); br.ReadInt32(); }
        if (br.ReadBoolean())
        {
            br.ReadBytes(4); br.ReadBoolean(); br.ReadByte(); br.ReadBoolean();
            br.ReadInt32(); br.ReadByte(); br.ReadBoolean(); br.ReadByte(); br.ReadByte();
            br.ReadInt32(); br.ReadByte(); br.ReadInt32(); br.ReadBoolean();
        }
        if (br.ReadBoolean())
        {
            br.ReadByte(); br.ReadSingle(); br.ReadByte();
            br.ReadBoolean(); br.ReadBoolean(); br.ReadSingle();
        }
    }

    private static void SkipToFirstPassRenderState(BinaryReader br)
    {
        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);
        SkipParameters(br);

        br.ReadInt32(); // technique count
        br.ReadString(); // technique name
        ReadAnnotationList(br);
        br.ReadInt32(); // pass count
        br.ReadString(); // pass name
        ReadAnnotationList(br);
        br.ReadInt32(); // vs index
        br.ReadInt32(); // ps index
    }

    private static IReadOnlyList<string> ReadTechniqueNames(byte[] bytes)
    {
        using var br = ReaderFor(bytes);
        SkipHeader(br);
        SkipConstantBuffers(br);
        SkipShaders(br);
        SkipParameters(br);

        var count = br.ReadInt32();
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var name = br.ReadString();
            names.Add(name);
            ReadAnnotationList(br);
            var passCount = br.ReadInt32();
            for (var p = 0; p < passCount; p++) SkipPass(br);
        }
        return names;
    }
}
