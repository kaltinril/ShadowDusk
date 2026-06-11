#nullable enable

using FluentAssertions;
using ShadowDusk.Core.Reflection;
using Xunit;
using static ShadowDusk.Core.Tests.Reflection.DxbcSyntheticBlobs;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Pure unit tests for <see cref="RdefReader"/> against synthetic DXBC containers built
/// by <see cref="DxbcSyntheticBlobs"/> (the CTAB-reader test pattern). Real-compiler
/// coverage — deep equality with the d3dcompiler_47 <c>D3DReflect</c> oracle on both
/// d3dcompiler and vkd3d DXBC — lives in
/// <c>ShadowDusk.HLSL.Tests.Reflection.DxbcReflectionParityTests</c> (Integration).
/// </summary>
public sealed class RdefReaderTests
{
    private const string SourceFile = "Test.fx";

    private static readonly SynthType Float4 = new(Class: 1, Type: 3, Rows: 1, Cols: 4, Elements: 0);
    private static readonly SynthType Float  = new(Class: 0, Type: 3, Rows: 1, Cols: 1, Elements: 0);

    // -------------------------------------------------------------------------
    // Happy path — field-by-field
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_TexturedCbufferBlob_ParsesAllFields()
    {
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings:
                [
                    new SynthBinding("SpriteTextureSampler", BindSampler, Dimension: 0, BindPoint: 0),
                    new SynthBinding("SpriteTexture",        BindTexture, Dimension: 4, BindPoint: 0),
                    new SynthBinding("$Globals",             BindCbuffer, Dimension: 0, BindPoint: 1),
                ],
                cbuffers:
                [
                    new SynthCbuffer("$Globals", Size: 16,
                        [new SynthVar("TintColor", StartOffset: 0, Size: 16, Float4)]),
                ])),
            ("ISGN", Signature(
                new SynthSigElement("SV_POSITION", Index: 0, SystemValue: 1, ComponentType: 3,
                    Register: 0, Mask: 0x0F, ReadWriteMask: 0x00),
                new SynthSigElement("TEXCOORD", Index: 0, SystemValue: 0, ComponentType: 3,
                    Register: 1, Mask: 0x03, ReadWriteMask: 0x03))),
            ("OSGN", Signature(
                new SynthSigElement("SV_TARGET", Index: 0, SystemValue: 0, ComponentType: 3,
                    Register: 0, Mask: 0x0F, ReadWriteMask: 0x00))));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "valid blob");
        ReflectedEffect effect = result.Value;

        effect.Textures.Should().ContainSingle().Which.Should().BeEquivalentTo(new TextureReflection
        {
            Name      = "SpriteTexture",
            BindSlot  = 0,
            Dimension = TextureDimension.Texture2D,
        });
        effect.Samplers.Should().ContainSingle().Which.Should().BeEquivalentTo(new SamplerReflection
        {
            Name     = "SpriteTextureSampler",
            BindSlot = 0,
        });

        ConstantBufferReflection cb = effect.ConstantBuffers.Should().ContainSingle().Subject;
        cb.Name.Should().Be("$Globals");
        cb.SizeBytes.Should().Be(16);
        cb.BindSlot.Should().Be(1, because: "the bind slot comes from the resource-binding table");
        VariableReflection tint = cb.Variables.Should().ContainSingle().Subject;
        tint.Name.Should().Be("TintColor");
        tint.StartOffset.Should().Be(0);
        tint.SizeBytes.Should().Be(16);
        tint.ParameterClass.Should().Be(EffectParameterClass.Vector);
        tint.ParameterType.Should().Be(EffectParameterType.Single);
        tint.Rows.Should().Be(1);
        tint.Columns.Should().Be(4);
        tint.Elements.Should().Be(0);
        tint.Members.Should().BeNull();

        effect.InputSignature.Should().HaveCount(2);
        effect.InputSignature[0].Should().BeEquivalentTo(new SignatureParameterReflection
        {
            SemanticName  = "SV_POSITION",
            SemanticIndex = 0,
            Register      = 0,
            SystemValue   = "Position",
            ComponentType = "Float32",
            Mask          = 0x0F,
        });
        effect.InputSignature[1].SemanticName.Should().Be("TEXCOORD");
        effect.InputSignature[1].SystemValue.Should().Be("Undefined");

        effect.OutputSignature.Should().ContainSingle()
            .Which.SystemValue.Should().Be("Target",
                because: "D3DReflect fixes up SV_Target's stored 0 by semantic name");

        effect.Parameters.Should().BeEmpty(because: "the parameter list is assembled downstream");
    }

    [Fact]
    public void Read_ArrayVariable_RoundsSizeToRegisterBoundary()
    {
        // float Arr[3]: RDEF reports 36 bytes (the trailing element unpadded); the
        // reader rounds arrays up to the 16-byte register boundary, as the previous
        // D3DReflect-based extractor did.
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 48,
                        [new SynthVar("Arr", StartOffset: 0, Size: 36,
                            Float with { Elements = 3 })]),
                ])));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue();
        VariableReflection arr = result.Value.ConstantBuffers[0].Variables[0];
        arr.SizeBytes.Should().Be(48);
        arr.Elements.Should().Be(3);
    }

    [Fact]
    public void Read_NonArrayVariable_KeepsExactSize()
    {
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 16,
                        [new SynthVar("Scale", StartOffset: 0, Size: 4, Float)]),
                ])));

        RdefReader.Read(dxbc, SourceFile).Value
            .ConstantBuffers[0].Variables[0].SizeBytes.Should().Be(4);
    }

    [Fact]
    public void Read_StructVariable_ParsesNestedMembers()
    {
        var attenuation = new SynthType(Class: 5, Type: 0, Rows: 1, Cols: 2, Elements: 0,
            Members:
            [
                ("Constant", 0u, Float),
                ("Linear",   4u, Float),
            ]);
        var light = new SynthType(Class: 5, Type: 0, Rows: 1, Cols: 7, Elements: 0,
            Members:
            [
                ("Dir",       0u, new SynthType(Class: 1, Type: 3, Rows: 1, Cols: 3, Elements: 0)),
                ("Intensity", 12u, Float),
                ("Atten",     16u, attenuation),
            ]);

        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("LightParams", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("LightParams", Size: 32,
                        [new SynthVar("Light", StartOffset: 0, Size: 24, light)]),
                ])));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "valid blob");
        VariableReflection variable = result.Value.ConstantBuffers[0].Variables[0];
        variable.ParameterClass.Should().Be(EffectParameterClass.Struct);
        variable.ParameterType.Should().Be(EffectParameterType.Void);
        variable.Members.Should().NotBeNull().And.HaveCount(3);

        variable.Members![0].Name.Should().Be("Dir");
        variable.Members[0].StartOffset.Should().Be(0);
        variable.Members[0].SizeBytes.Should().Be(0, because: "D3DReflect exposes no member size");
        variable.Members[0].ParameterClass.Should().Be(EffectParameterClass.Vector);
        variable.Members[0].Columns.Should().Be(3);

        variable.Members[1].Name.Should().Be("Intensity");
        variable.Members[1].StartOffset.Should().Be(12);

        VariableReflection nested = variable.Members[2];
        nested.Name.Should().Be("Atten");
        nested.StartOffset.Should().Be(16);
        nested.ParameterClass.Should().Be(EffectParameterClass.Struct);
        nested.Members.Should().NotBeNull().And.HaveCount(2);
        nested.Members![1].Name.Should().Be("Linear");
        nested.Members[1].StartOffset.Should().Be(4);
    }

    [Fact]
    public void Read_EmptyCbuffer_IsDropped()
    {
        // mgfxc emits no cbuffer record for an empty $Globals (e.g. a texture-only PS).
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("$Globals", BindCbuffer, 0, 0)],
                cbuffers: [new SynthCbuffer("$Globals", Size: 0, Vars: [])])));

        RdefReader.Read(dxbc, SourceFile).Value.ConstantBuffers.Should().BeEmpty();
    }

    [Fact]
    public void Read_DepthOutput_ReportsRegisterMinusOneAndDepthFixUp()
    {
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target, bindings: [], cbuffers: [])),
            ("OSGN", Signature(
                new SynthSigElement("SV_TARGET", Index: 0, SystemValue: 0, ComponentType: 3,
                    Register: 0, Mask: 0x0F, ReadWriteMask: 0x00),
                new SynthSigElement("SV_Depth", Index: 0, SystemValue: 0, ComponentType: 3,
                    Register: 0xFFFFFFFF, Mask: 0x01, ReadWriteMask: 0x0E))));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue();
        result.Value.OutputSignature.Should().HaveCount(2);
        result.Value.OutputSignature[0].SystemValue.Should().Be("Target");
        result.Value.OutputSignature[1].SystemValue.Should().Be("Depth");
        result.Value.OutputSignature[1].Register.Should().Be(-1);
    }

    [Fact]
    public void Read_TextureDimensions_MapLikeTheD3DReflectExtractor()
    {
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings:
                [
                    new SynthBinding("Tex1D",   BindTexture, Dimension: 2,  BindPoint: 0),
                    new SynthBinding("Tex2DA",  BindTexture, Dimension: 5,  BindPoint: 1),
                    new SynthBinding("Volume",  BindTexture, Dimension: 8,  BindPoint: 2),
                    new SynthBinding("EnvMap",  BindTexture, Dimension: 9,  BindPoint: 3),
                    new SynthBinding("CubeArr", BindTexture, Dimension: 10, BindPoint: 4),
                    new SynthBinding("Buf",     BindTexture, Dimension: 1,  BindPoint: 5),
                ],
                cbuffers: [])));

        var textures = RdefReader.Read(dxbc, SourceFile).Value.Textures;

        textures.Select(t => t.Dimension).Should().Equal(
            TextureDimension.Texture1D,
            TextureDimension.Texture2D,
            TextureDimension.Texture3D,
            TextureDimension.TextureCube,
            TextureDimension.TextureCube,
            TextureDimension.Unknown);
        textures.Select(t => t.BindSlot).Should().Equal(0, 1, 2, 3, 4, 5);
    }

    [Fact]
    public void Read_Sm4Rdef_ParsesWithTheShorterVariableRecords()
    {
        // SM4 RDEF has no 'RD11' block and 24-byte variable records.
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps41Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 16,
                        [new SynthVar("Color", StartOffset: 0, Size: 16, Float4)]),
                ])));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "valid SM4 blob");
        result.Value.ConstantBuffers[0].Variables[0].Name.Should().Be("Color");
    }

    [Fact]
    public void Read_UnknownSystemValueAndComponentType_RenderNumerically()
    {
        // Mirrors Vortice's enum ToString for undefined values (the previous extractor's
        // behavior): the raw number as a string.
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target, bindings: [], cbuffers: [])),
            ("ISGN", Signature(
                new SynthSigElement("WEIRD", Index: 0, SystemValue: 99, ComponentType: 7,
                    Register: 0, Mask: 0x0F, ReadWriteMask: 0x0F))));

        var sig = RdefReader.Read(dxbc, SourceFile).Value.InputSignature[0];

        sig.SystemValue.Should().Be("99");
        sig.ComponentType.Should().Be("7");
    }

    [Fact]
    public void Read_MissingSignatureChunks_YieldEmptySignatures()
    {
        byte[] dxbc = Container(("RDEF", Rdef(Ps50Target, bindings: [], cbuffers: [])));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsSuccess.Should().BeTrue();
        result.Value.InputSignature.Should().BeEmpty();
        result.Value.OutputSignature.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Failure paths — loud, structured errors (never throws)
    // -------------------------------------------------------------------------

    [Fact]
    public void Read_NotADxbcContainer_Fails()
    {
        var result = RdefReader.Read(new byte[64], SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0101");
        result.Error.File.Should().Be(SourceFile);
        result.Error.Message.Should().Contain("not a DXBC container");
    }

    [Fact]
    public void Read_TooShortForHeader_Fails()
    {
        RdefReader.Read(new byte[8], SourceFile).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Read_MissingRdefChunk_Fails()
    {
        byte[] dxbc = Container(("OSGN", Signature()));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("no RDEF chunk");
    }

    [Fact]
    public void Read_ChunkOffsetOutOfBounds_Fails()
    {
        byte[] dxbc = Container(("RDEF", Rdef(Ps50Target, bindings: [], cbuffers: [])));
        // Corrupt the first chunk offset to point past the end.
        BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(dxbc, 32);

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("out of bounds");
    }

    [Fact]
    public void Read_TruncatedRdef_Fails()
    {
        byte[] rdef = Rdef(Ps50Target,
            bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
            cbuffers:
            [
                new SynthCbuffer("Params", Size: 16,
                    [new SynthVar("Color", StartOffset: 0, Size: 16, Float4)]),
            ]);
        byte[] dxbc = Container(("RDEF", rdef.AsSpan(0, 32).ToArray()));

        RdefReader.Read(dxbc, SourceFile).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Read_UnmappedVariableClass_Fails()
    {
        // Class 6 = D3D_SVC_INTERFACE_CLASS — unmapped by the previous extractor too.
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 16,
                        [new SynthVar("Iface", StartOffset: 0, Size: 16,
                            new SynthType(Class: 6, Type: 0, Rows: 1, Cols: 1, Elements: 0))]),
                ])));

        var result = RdefReader.Read(dxbc, SourceFile);

        result.IsFailure.Should().BeTrue();
        result.Error.Message.Should().Contain("unmapped shader variable class");
    }

    [Fact]
    public void Read_UnmappedVariableType_Fails()
    {
        // Type 39 = D3D_SVT_DOUBLE — unmapped by the previous extractor too.
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 16,
                        [new SynthVar("Dbl", StartOffset: 0, Size: 8,
                            new SynthType(Class: 0, Type: 39, Rows: 1, Cols: 1, Elements: 0))]),
                ])));

        RdefReader.Read(dxbc, SourceFile).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Read_IsDeterministic()
    {
        byte[] dxbc = Container(
            ("RDEF", Rdef(Ps50Target,
                bindings: [new SynthBinding("Params", BindCbuffer, 0, 0)],
                cbuffers:
                [
                    new SynthCbuffer("Params", Size: 16,
                        [new SynthVar("Color", StartOffset: 0, Size: 16, Float4)]),
                ])));

        var first  = RdefReader.Read(dxbc, SourceFile);
        var second = RdefReader.Read(dxbc, SourceFile);

        first.IsSuccess.Should().BeTrue();
        second.Value.Should().BeEquivalentTo(first.Value, options => options.WithStrictOrdering());
    }
}
