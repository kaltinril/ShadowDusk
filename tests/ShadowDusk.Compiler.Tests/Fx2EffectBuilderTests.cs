#nullable enable

using System.Text;
using FluentAssertions;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.Ast;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for the internal <see cref="Fx2EffectBuilder"/> (Phase 39, FNA fx_2_0
/// target): CTAB merging, parameter ordering, sampler-state and render-state mapping into
/// the D3D9 value domain (docs/fx2-binary-format.md §8.2), and the technique/pass
/// passthrough. No disk, no processes — all inputs are hand-built records.
///
/// The render-state facts intentionally pin every MonoGame-ordinal → D3D9 value pair the
/// builder maps: a wrong value here silently breaks rendering in FNA rather than failing.
/// </summary>
public sealed class Fx2EffectBuilderTests
{
    private const string SourceFile = "effect.fx";

    // ---------------------------------------------------------------------------
    // Helpers — small builders with sensible defaults keep the cases readable.
    // ---------------------------------------------------------------------------

    /// <summary>A CTAB constant; defaults model a non-array float4 vector in c0.</summary>
    private static CtabConstant Constant(
        string name,
        CtabRegisterSet registerSet = CtabRegisterSet.Float4,
        int @class = 1,   // D3DXPC_VECTOR
        int type = 3,     // D3DXPT_FLOAT
        int rows = 1,
        int columns = 4,
        int elements = 1, // CTAB writes 1 for non-arrays
        int registerIndex = 0,
        int registerCount = 1,
        IReadOnlyList<float>? defaultValue = null)
        => new(name, registerSet, registerIndex, registerCount,
               @class, type, rows, columns, elements, defaultValue);

    /// <summary>A CTAB sampler constant (class 4 OBJECT; type 12 = SAMPLER2D).</summary>
    private static CtabConstant SamplerConstant(string name, int type = 12, int registerIndex = 0)
        => Constant(name, CtabRegisterSet.Sampler, @class: 4, type: type,
                    rows: 1, columns: 1, registerIndex: registerIndex);

    /// <summary>A CTAB float4x4 column-major matrix constant (class 3).</summary>
    private static CtabConstant MatrixConstant(string name, int rows = 4, int columns = 4)
        => Constant(name, @class: 3, rows: rows, columns: columns, registerCount: 4);

    private static CtabTable PsCtab(params CtabConstant[] constants)
        => new(0xFFFF0200, "test", "ps_2_0", constants);

    private static CtabTable VsCtab(params CtabConstant[] constants)
        => new(0xFFFE0200, "test", "vs_2_0", constants);

    private static SamplerInfo Sampler(
        string name, string? textureReference, params (string Key, string Value)[] entries)
        => new()
        {
            Name = name,
            SamplerType = "sampler2D",
            TextureReference = textureReference,
            StateEntries = entries
                .Select(e => new SamplerStateEntry(e.Key, e.Value, SourceSpan.Unknown))
                .ToList(),
            Span = SourceSpan.Unknown,
        };

    private static readonly IReadOnlyList<Fx2TechniqueSource> DefaultTechniques =
        [new Fx2TechniqueSource("T", [new Fx2PassSource("P0", -1, 0, new RenderStateBlock())])];

    private static Result<Fx2EffectDesc, ShaderError> Build(
        IReadOnlyList<CtabTable> ctabs,
        IReadOnlyList<SamplerInfo>? samplerInfos = null,
        IReadOnlyList<Fx2TechniqueSource>? techniques = null,
        IReadOnlyList<Fx2Shader>? shaders = null)
        => Fx2EffectBuilder.Build(
            techniques ?? DefaultTechniques,
            shaders ?? Array.Empty<Fx2Shader>(),
            ctabs,
            samplerInfos ?? Array.Empty<SamplerInfo>(),
            SourceFile);

    // ---------------------------------------------------------------------------
    // 1. Parameter assembly & ordering: numerics, then textures, then samplers —
    //    FNA requires every texture to precede the samplers that reference it.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_NumericTextureSampler_OrdersNumericsThenTexturesThenSamplers()
    {
        var result = Build(
            ctabs: [PsCtab(Constant("Tint"), SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", "tex", ("MipFilter", "LINEAR"))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");

        IReadOnlyList<Fx2Parameter> parameters = result.Value.Parameters;
        parameters.Select(p => p.Name).Should().Equal(["Tint", "tex", "s0"],
            because: "FNA's loader requires textures to precede the samplers that bind them");

        Fx2Parameter tint = parameters[0];
        tint.Class.Should().Be(1, because: "CTAB class VECTOR flows through");
        tint.Type.Should().Be(3, because: "CTAB type FLOAT flows through");
        tint.Rows.Should().Be(1);
        tint.Columns.Should().Be(4);
        tint.SamplerStates.Should().BeEmpty();

        Fx2Parameter tex = parameters[1];
        tex.Class.Should().Be(4, because: "texture parameters are OBJECT class");
        tex.Type.Should().Be(5, because: "the undimensioned TEXTURE type is emitted");
        tex.SamplerStates.Should().BeEmpty();

        Fx2Parameter s0 = parameters[2];
        s0.Class.Should().Be(4);
        s0.Type.Should().Be(12, because: "the sampler type comes from the CTAB (SAMPLER2D)");

        s0.SamplerStates.Should().HaveCount(2);
        s0.SamplerStates[0].Operation.Should().Be(164, because: "the Texture state must come first");
        s0.SamplerStates[0].TextureParameterName.Should().Be("tex");
        s0.SamplerStates[0].IntValue.Should().BeNull();
        s0.SamplerStates[0].FloatValue.Should().BeNull();
        s0.SamplerStates[1].Operation.Should().Be(171, because: "MipFilter is on-disk op 171");
        s0.SamplerStates[1].IntValue.Should().Be(2, because: "LINEAR is D3DTEXF_LINEAR = 2");
        s0.SamplerStates[1].TextureParameterName.Should().BeNull();
        s0.SamplerStates[1].FloatValue.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // 2. Cross-stage merge: the same global reflected from both stages must
    //    collapse to one parameter; conflicting shapes must fail loudly.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_SharedGlobalAcrossStages_MergesToOneParameter()
    {
        var result = Build(
            ctabs: [VsCtab(MatrixConstant("WorldViewProj")), PsCtab(MatrixConstant("WorldViewProj"))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "shapes agree");

        Fx2Parameter merged = result.Value.Parameters.Should().ContainSingle().Subject;
        merged.Name.Should().Be("WorldViewProj");
        merged.Class.Should().Be(3, because: "MATRIX_COLUMNS class is preserved");
        merged.Rows.Should().Be(4);
        merged.Columns.Should().Be(4);
    }

    [Fact]
    public void Build_ConflictingShapesAcrossStages_FailsWithSD0303NamingTheGlobal()
    {
        var result = Build(
            ctabs:
            [
                VsCtab(MatrixConstant("WorldViewProj", rows: 4, columns: 4)),
                PsCtab(Constant("WorldViewProj", rows: 1, columns: 4)), // vector in the PS
            ]);

        result.IsFailure.Should().BeTrue(because: "a 4x4 matrix cannot merge with a 1x4 vector");
        result.Error.Code.Should().Be("SD0303");
        result.Error.File.Should().Be(SourceFile);
        result.Error.Message.Should().Contain("WorldViewProj",
            because: "the diagnostic must name the conflicting global");
        result.Error.Message.Should().Contain("conflicting shapes");
    }

    [Fact]
    public void Build_DefaultValue_LaterStageFillsInWhenFirstStageHasNone()
    {
        float[] psDefault = [1f, 2f, 3f, 4f];
        var result = Build(
            ctabs:
            [
                VsCtab(Constant("Tint", defaultValue: null)),
                PsCtab(Constant("Tint", defaultValue: psDefault)),
            ]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "shapes agree");
        result.Value.Parameters.Should().ContainSingle()
            .Which.DefaultValue.Should().Equal(psDefault,
                because: "the first non-null default value wins");
    }

    [Fact]
    public void Build_DefaultValue_FirstNonNullWinsOverLaterStages()
    {
        float[] vsDefault = [9f, 9f, 9f, 9f];
        var result = Build(
            ctabs:
            [
                VsCtab(Constant("Tint", defaultValue: vsDefault)),
                PsCtab(Constant("Tint", defaultValue: [1f, 2f, 3f, 4f])),
            ]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "shapes agree");
        result.Value.Parameters.Should().ContainSingle()
            .Which.DefaultValue.Should().Equal(vsDefault,
                because: "an already-present default must not be overwritten by a later stage");
    }

    // ---------------------------------------------------------------------------
    // 3. CTAB Elements normalization: CTAB writes 1 for non-arrays; on disk in
    //    fx_2_0, 0 means "not an array" (0 and 1 are distinct).
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0)] // CTAB non-array marker → on-disk "not an array"
    [InlineData(4, 4)] // real arrays flow through unchanged
    public void Build_CtabElements_NormalizedForDisk(int ctabElements, int expectedElements)
    {
        var result = Build(ctabs: [PsCtab(Constant("Values", elements: ctabElements))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");
        result.Value.Parameters.Should().ContainSingle()
            .Which.Elements.Should().Be(expectedElements);
    }

    // ---------------------------------------------------------------------------
    // 4. Struct globals (CTAB class 5) are rejected up front.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_StructGlobal_FailsNamingTheStruct()
    {
        var result = Build(ctabs: [PsCtab(Constant("Material", @class: 5))]);

        result.IsFailure.Should().BeTrue(because: "struct effect parameters are unsupported for FNA");
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain("Material");
        result.Error.Message.Should().Contain("struct");
    }

    // ---------------------------------------------------------------------------
    // 4b. Sampler arrays (CTAB Elements > 1) are rejected up front: fx_2_0 sampler
    //     arrays need per-element value objects, which the writer does not model —
    //     failing loudly beats emitting a parameter shape that diverges from fxc's.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_SamplerArray_FailsWithSD0303NamingSamplerAndElementCount()
    {
        var result = Build(
            ctabs: [PsCtab(Constant("samps", CtabRegisterSet.Sampler,
                @class: 4, type: 12, rows: 1, columns: 1, elements: 2))]);

        result.IsFailure.Should().BeTrue(because: "sampler arrays are unsupported for the FNA target");
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain("samps",
            because: "the diagnostic must name the offending sampler");
        result.Error.Message.Should().Contain("[2]",
            because: "the diagnostic must show the array element count");
    }

    // ---------------------------------------------------------------------------
    // 5./6. Samplers with partial or absent sampler_state metadata.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_BareSamplerWithoutSamplerInfo_EmitsSamplerWithZeroStates()
    {
        var result = Build(ctabs: [PsCtab(SamplerConstant("s0"))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "a bare sampler is valid");

        Fx2Parameter s0 = result.Value.Parameters.Should().ContainSingle().Subject;
        s0.Name.Should().Be("s0");
        s0.Class.Should().Be(4);
        s0.Type.Should().Be(12);
        s0.SamplerStates.Should().BeEmpty(
            because: "no SamplerInfo means no Texture state and no state entries");
    }

    [Fact]
    public void Build_SamplerInfoWithoutTextureReference_EmitsEntryStatesOnlyNoTextureState()
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, ("MinFilter", "Point"))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");

        Fx2Parameter s0 = result.Value.Parameters.Should().ContainSingle(
            because: "no texture reference means no texture parameter is synthesized").Subject;
        Fx2SamplerState state = s0.SamplerStates.Should().ContainSingle(
            because: "only the MinFilter entry maps; op 164 requires a texture reference").Subject;
        state.Operation.Should().Be(170);
        state.IntValue.Should().Be(1);
        state.TextureParameterName.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // 7. Sampler-state mapping table: keys → on-disk 164-based ops; value
    //    spellings → D3D9 enum values (case-insensitive; numeric literals pass).
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("MinFilter", "LINEAR", 170, 2)]
    [InlineData("MagFilter", "Point", 169, 1)] // case-insensitive spelling
    [InlineData("MipFilter", "ANISOTROPIC", 171, 3)]
    [InlineData("MinFilter", "None", 170, 0)]
    [InlineData("AddressU", "WRAP", 165, 1)]
    [InlineData("AddressV", "Clamp", 166, 3)]
    [InlineData("AddressW", "MIRRORONCE", 167, 5)]
    [InlineData("MaxMipLevel", "3", 173, 3)]
    [InlineData("MaxAnisotropy", "4", 174, 4)]
    [InlineData("MinFilter", "2", 170, 2)] // numeric literal accepted verbatim
    public void Build_SamplerStateEntry_MapsToExpectedOpAndIntValue(
        string key, string value, int expectedOp, int expectedIntValue)
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, (key, value))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");

        Fx2SamplerState state = result.Value.Parameters.Single().SamplerStates
            .Should().ContainSingle().Subject;
        state.Operation.Should().Be(expectedOp);
        state.IntValue.Should().Be(expectedIntValue);
        state.FloatValue.Should().BeNull();
        state.TextureParameterName.Should().BeNull();
    }

    [Fact]
    public void Build_MipMapLodBias_MapsToOp172WithFloatValue()
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, ("MipMapLodBias", "0.5"))]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");

        Fx2SamplerState state = result.Value.Parameters.Single().SamplerStates
            .Should().ContainSingle().Subject;
        state.Operation.Should().Be(172);
        state.FloatValue.Should().Be(0.5f, because: "MipMapLodBias is the float-valued state");
        state.IntValue.Should().BeNull();
    }

    [Theory]
    [InlineData("BorderColor", "0")]
    [InlineData("SRGBTexture", "true")]
    [InlineData("ElementIndex", "0")]
    [InlineData("DMapOffset", "0")]
    public void Build_FnaThrowingSamplerStateKey_FailsWithHelpfulMessage(string key, string value)
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, (key, value))]);

        result.IsFailure.Should().BeTrue(
            because: $"FNA's runtime throws NotImplementedException on '{key}'");
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain(key,
            because: "the diagnostic must name the offending state");
        result.Error.Message.Should().Contain("s0",
            because: "the diagnostic must name the sampler");
    }

    [Fact]
    public void Build_UnknownSamplerStateKey_FailsListingSupportedKeys()
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, ("Filter", "LINEAR"))]);

        result.IsFailure.Should().BeTrue(because: "'Filter' is not a recognized fx_2_0 sampler state");
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain("Filter");
        result.Error.Message.Should().Contain("MinFilter",
            because: "the diagnostic should steer the user to the supported keys");
    }

    [Fact]
    public void Build_NonNumericMaxAnisotropy_Fails()
    {
        var result = Build(
            ctabs: [PsCtab(SamplerConstant("s0"))],
            samplerInfos: [Sampler("s0", textureReference: null, ("MaxAnisotropy", "fast"))]);

        result.IsFailure.Should().BeTrue(because: "MaxAnisotropy requires an integer value");
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain("fast",
            because: "the diagnostic must show the offending value");
    }

    // ---------------------------------------------------------------------------
    // 8. MapRenderStates — pins every MonoGame-ordinal → D3D9 value pair
    //    (docs/fx2-binary-format.md §8.2). A wrong value silently breaks rendering.
    // ---------------------------------------------------------------------------

    private static void AssertSingleRenderState(RenderStateBlock block, Fx2RenderState expected)
        => Fx2EffectBuilder.MapRenderStates(block)
            .Should().ContainSingle().Which.Should().Be(expected);

    [Fact]
    public void MapRenderStates_EmptyBlock_ProducesNoStates()
        => Fx2EffectBuilder.MapRenderStates(new RenderStateBlock()).Should().BeEmpty();

    // Blend.
    [Fact]
    public void MapRenderStates_AlphaBlendEnableTrue_Is13_1()
        => AssertSingleRenderState(new RenderStateBlock { AlphaBlendEnable = true },
            new Fx2RenderState(13, 1));

    [Fact]
    public void MapRenderStates_ColorSourceBlendSourceAlpha_Is6_5()
        => AssertSingleRenderState(new RenderStateBlock { ColorSourceBlend = BlendValue.SourceAlpha },
            new Fx2RenderState(6, 5));

    [Fact]
    public void MapRenderStates_ColorDestinationBlendInverseSourceAlpha_Is7_6()
        => AssertSingleRenderState(new RenderStateBlock { ColorDestinationBlend = BlendValue.InverseSourceAlpha },
            new Fx2RenderState(7, 6));

    [Fact]
    public void MapRenderStates_ColorBlendFunctionAdd_Is75_1()
        => AssertSingleRenderState(new RenderStateBlock { ColorBlendFunction = BlendFunctionValue.Add },
            new Fx2RenderState(75, 1));

    [Fact]
    public void MapRenderStates_AlphaSourceBlendOne_Is100_2()
        => AssertSingleRenderState(new RenderStateBlock { AlphaSourceBlend = BlendValue.One },
            new Fx2RenderState(100, 2));

    [Fact]
    public void MapRenderStates_AlphaDestinationBlendZero_Is101_1()
        => AssertSingleRenderState(new RenderStateBlock { AlphaDestinationBlend = BlendValue.Zero },
            new Fx2RenderState(101, 1));

    [Fact]
    public void MapRenderStates_AlphaBlendFunctionMax_Is102_5()
        => AssertSingleRenderState(new RenderStateBlock { AlphaBlendFunction = BlendFunctionValue.Max },
            new Fx2RenderState(102, 5));

    [Fact]
    public void MapRenderStates_ColorWriteChannels15_Is73_15()
        => AssertSingleRenderState(new RenderStateBlock { ColorWriteChannels = 15 },
            new Fx2RenderState(73, 15));

    // Depth/stencil.
    [Fact]
    public void MapRenderStates_DepthBufferEnableTrue_Is0_1()
        => AssertSingleRenderState(new RenderStateBlock { DepthBufferEnable = true },
            new Fx2RenderState(0, 1));

    [Fact]
    public void MapRenderStates_DepthBufferWriteEnableFalse_Is3_0()
        => AssertSingleRenderState(new RenderStateBlock { DepthBufferWriteEnable = false },
            new Fx2RenderState(3, 0));

    [Fact]
    public void MapRenderStates_DepthBufferFunctionLessEqual_Is9_4()
        => AssertSingleRenderState(new RenderStateBlock { DepthBufferFunction = CompareFunctionValue.LessEqual },
            new Fx2RenderState(9, 4));

    [Fact]
    public void MapRenderStates_DepthBufferFunctionAlways_Is9_8()
        => AssertSingleRenderState(new RenderStateBlock { DepthBufferFunction = CompareFunctionValue.Always },
            new Fx2RenderState(9, 8));

    [Fact]
    public void MapRenderStates_StencilFailKeep_Is23_1()
        => AssertSingleRenderState(new RenderStateBlock { StencilFail = StencilOperationValue.Keep },
            new Fx2RenderState(23, 1));

    [Fact]
    public void MapRenderStates_StencilDepthBufferFailIncrement_Is24_7()
        => AssertSingleRenderState(new RenderStateBlock { StencilDepthBufferFail = StencilOperationValue.Increment },
            new Fx2RenderState(24, 7));

    [Fact]
    public void MapRenderStates_StencilPassReplace_Is25_3()
        => AssertSingleRenderState(new RenderStateBlock { StencilPass = StencilOperationValue.Replace },
            new Fx2RenderState(25, 3));

    [Fact]
    public void MapRenderStates_StencilFunctionNever_Is26_1()
        => AssertSingleRenderState(new RenderStateBlock { StencilFunction = CompareFunctionValue.Never },
            new Fx2RenderState(26, 1));

    [Fact]
    public void MapRenderStates_ReferenceStencil42_Is27_42()
        => AssertSingleRenderState(new RenderStateBlock { ReferenceStencil = 42 },
            new Fx2RenderState(27, 42));

    // Rasterizer.
    [Fact]
    public void MapRenderStates_CullModeNone_Is8_1()
        => AssertSingleRenderState(new RenderStateBlock { CullMode = CullModeValue.None },
            new Fx2RenderState(8, 1));

    [Fact]
    public void MapRenderStates_FillModeWireFrame_Is1_2()
        => AssertSingleRenderState(new RenderStateBlock { FillMode = FillModeValue.WireFrame },
            new Fx2RenderState(1, 2));

    [Fact]
    public void MapRenderStates_FillModeSolid_Is1_3()
        => AssertSingleRenderState(new RenderStateBlock { FillMode = FillModeValue.Solid },
            new Fx2RenderState(1, 3));

    [Fact]
    public void MapRenderStates_ScissorTestEnableTrue_Is78_1()
        => AssertSingleRenderState(new RenderStateBlock { ScissorTestEnable = true },
            new Fx2RenderState(78, 1));

    [Fact]
    public void MapRenderStates_MultiSampleAntiAliasFalse_Is67_0()
        => AssertSingleRenderState(new RenderStateBlock { MultiSampleAntiAlias = false },
            new Fx2RenderState(67, 0));

    [Fact]
    public void MapRenderStates_DepthBiasHalf_Is98_FloatBits()
        => AssertSingleRenderState(new RenderStateBlock { DepthBias = 0.5f },
            new Fx2RenderState(98, BitConverter.SingleToUInt32Bits(0.5f), IsFloat: true));

    [Fact]
    public void MapRenderStates_SlopeScaleDepthBias_Is79_FloatBits()
        => AssertSingleRenderState(new RenderStateBlock { SlopeScaleDepthBias = 2.0f },
            new Fx2RenderState(79, BitConverter.SingleToUInt32Bits(2.0f), IsFloat: true));

    // FNA-only states (honored † ops MGFX has no analog for; docs/fx2-binary-format.md §8.2).
    [Fact]
    public void MapRenderStates_SeparateAlphaBlendEnableTrue_Is99_1()
        => AssertSingleRenderState(new RenderStateBlock { SeparateAlphaBlendEnable = true },
            new Fx2RenderState(99, 1));

    [Fact]
    public void MapRenderStates_SeparateAlphaBlendEnableFalse_Is99_0()
        => AssertSingleRenderState(new RenderStateBlock { SeparateAlphaBlendEnable = false },
            new Fx2RenderState(99, 0));

    [Fact]
    public void MapRenderStates_BlendFactor_Is96_RawD3DColorDword()
        => AssertSingleRenderState(new RenderStateBlock { BlendFactor = 0x80FF8080u },
            new Fx2RenderState(96, 0x80FF8080u));

    [Fact]
    public void MapRenderStates_MultiSampleMask_Is68_RawDword()
        => AssertSingleRenderState(new RenderStateBlock { MultiSampleMask = 0xFFFF0000u },
            new Fx2RenderState(68, 0xFFFF0000u));

    [Fact]
    public void MapRenderStates_TwoSidedStencilModeTrue_Is88_1()
        => AssertSingleRenderState(new RenderStateBlock { TwoSidedStencilMode = true },
            new Fx2RenderState(88, 1));

    [Fact]
    public void MapRenderStates_CcwStencilFailKeep_Is89_1()
        => AssertSingleRenderState(new RenderStateBlock { CounterClockwiseStencilFail = StencilOperationValue.Keep },
            new Fx2RenderState(89, 1));

    [Fact]
    public void MapRenderStates_CcwStencilZFailIncrement_Is90_7()
        => AssertSingleRenderState(new RenderStateBlock { CounterClockwiseStencilDepthBufferFail = StencilOperationValue.Increment },
            new Fx2RenderState(90, 7));

    [Fact]
    public void MapRenderStates_CcwStencilPassReplace_Is91_3()
        => AssertSingleRenderState(new RenderStateBlock { CounterClockwiseStencilPass = StencilOperationValue.Replace },
            new Fx2RenderState(91, 3));

    [Fact]
    public void MapRenderStates_CcwStencilFuncAlways_Is92_8()
        => AssertSingleRenderState(new RenderStateBlock { CounterClockwiseStencilFunction = CompareFunctionValue.Always },
            new Fx2RenderState(92, 8));

    [Fact]
    public void MapRenderStates_ColorWriteChannels1_Is93_RawBits()
        => AssertSingleRenderState(new RenderStateBlock { ColorWriteChannels1 = 3 },
            new Fx2RenderState(93, 3));

    [Fact]
    public void MapRenderStates_ColorWriteChannels2_Is94_RawBits()
        => AssertSingleRenderState(new RenderStateBlock { ColorWriteChannels2 = 8 },
            new Fx2RenderState(94, 8));

    [Fact]
    public void MapRenderStates_ColorWriteChannels3_Is95_RawBits()
        => AssertSingleRenderState(new RenderStateBlock { ColorWriteChannels3 = 15 },
            new Fx2RenderState(95, 15));

    // ---------------------------------------------------------------------------
    // 8b. Combined old + new states: every pair is emitted, in the deterministic
    //     blend → depth/stencil → rasterizer order MapRenderStates pins.
    // ---------------------------------------------------------------------------

    [Fact]
    public void MapRenderStates_CombinedOldAndNewStates_EmitsAllPairsInDeterministicOrder()
    {
        var block = new RenderStateBlock
        {
            // Old (pre-existing) states.
            AlphaBlendEnable = true,
            ColorSourceBlend = BlendValue.SourceAlpha,
            AlphaSourceBlend = BlendValue.One,
            ColorWriteChannels = 15,
            StencilEnable = true,
            StencilFunction = CompareFunctionValue.Always,
            CullMode = CullModeValue.None,
            MultiSampleAntiAlias = false,
            DepthBias = 0.5f,
            // New (FNA-only) states.
            SeparateAlphaBlendEnable = true,
            BlendFactor = 0x80FF8080u,
            ColorWriteChannels1 = 3,
            ColorWriteChannels2 = 8,
            ColorWriteChannels3 = 1,
            TwoSidedStencilMode = true,
            CounterClockwiseStencilFail = StencilOperationValue.Keep,
            CounterClockwiseStencilDepthBufferFail = StencilOperationValue.Increment,
            CounterClockwiseStencilPass = StencilOperationValue.Replace,
            CounterClockwiseStencilFunction = CompareFunctionValue.Less,
            MultiSampleMask = 0xFFFF0000u,
        };

        Fx2EffectBuilder.MapRenderStates(block).Should().Equal(
            // Blend group.
            new Fx2RenderState(13, 1),            // AlphaBlendEnable = true
            new Fx2RenderState(6, 5),             // SrcBlend = SrcAlpha
            new Fx2RenderState(99, 1),            // SeparateAlphaBlendEnable = true
            new Fx2RenderState(100, 2),           // SrcBlendAlpha = One
            new Fx2RenderState(96, 0x80FF8080u),  // BlendFactor
            new Fx2RenderState(73, 15),           // ColorWriteEnable
            new Fx2RenderState(93, 3),            // ColorWriteEnable1
            new Fx2RenderState(94, 8),            // ColorWriteEnable2
            new Fx2RenderState(95, 1),            // ColorWriteEnable3
            // Depth/stencil group.
            new Fx2RenderState(22, 1),            // StencilEnable = true
            new Fx2RenderState(26, 8),            // StencilFunc = Always
            new Fx2RenderState(88, 1),            // TwoSidedStencilMode = true
            new Fx2RenderState(89, 1),            // CCW_StencilFail = Keep
            new Fx2RenderState(90, 7),            // CCW_StencilZFail = Incr
            new Fx2RenderState(91, 3),            // CCW_StencilPass = Replace
            new Fx2RenderState(92, 2),            // CCW_StencilFunc = Less
            // Rasterizer group.
            new Fx2RenderState(8, 1),             // CullMode = None
            new Fx2RenderState(67, 0),            // MultiSampleAntiAlias = false
            new Fx2RenderState(68, 0xFFFF0000u),  // MultiSampleMask
            new Fx2RenderState(98, BitConverter.SingleToUInt32Bits(0.5f), IsFloat: true)); // DepthBias
    }

    [Fact]
    public void MapRenderStates_AllNewOps_AreAcceptedByFx2EffectWriter()
    {
        // Guards against an op typo the unit pins above can't catch alone: the writer
        // restricts pass render states to FNA's honored set, so every new op must be in it.
        IReadOnlyList<Fx2RenderState> states = Fx2EffectBuilder.MapRenderStates(new RenderStateBlock
        {
            SeparateAlphaBlendEnable = true,
            BlendFactor = 0x80FF8080u,
            ColorWriteChannels1 = 3,
            ColorWriteChannels2 = 8,
            ColorWriteChannels3 = 1,
            TwoSidedStencilMode = true,
            CounterClockwiseStencilFail = StencilOperationValue.Keep,
            CounterClockwiseStencilDepthBufferFail = StencilOperationValue.Increment,
            CounterClockwiseStencilPass = StencilOperationValue.Replace,
            CounterClockwiseStencilFunction = CompareFunctionValue.Less,
            MultiSampleMask = 0xFFFF0000u,
        });

        var effect = new Fx2EffectDesc
        {
            Parameters = [],
            Techniques = [new Fx2Technique("T", [new Fx2Pass("P0", -1, -1, states)])],
            Shaders = [],
        };

        var written = new Fx2EffectWriter().Write(effect);
        written.IsSuccess.Should().BeTrue(
            because: written.IsFailure ? written.Error.Message : "all new ops are in FNA's honored set");
    }

    // ---------------------------------------------------------------------------
    // 8c. Known-FNA-throwing render states (the non-honored §8.2 ops, e.g.
    //     AlphaTestEnable / fog / point sprites) must fail SD0303 loudly: the fxc
    //     build of the same .fx throws NotImplementedException in FNA at
    //     EffectPass.Apply, so silently dropping them would mask author error.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_PassWithFnaThrowingState_FailsWithSD0303NamingTheState()
    {
        var result = Build(
            ctabs: [],
            techniques:
            [
                new Fx2TechniqueSource("T",
                [
                    new Fx2PassSource("P0", -1, 0, new RenderStateBlock
                    {
                        KnownFnaThrowingStates = ["AlphaTestEnable"],
                    }),
                ]),
            ]);

        result.IsFailure.Should().BeTrue(
            because: "FNA throws NotImplementedException on AlphaTestEnable at EffectPass.Apply");
        result.Error.Code.Should().Be("SD0303");
        result.Error.File.Should().Be(SourceFile);
        result.Error.Message.Should().Contain("AlphaTestEnable",
            because: "the diagnostic must name the offending state");
        result.Error.Message.Should().Contain("P0",
            because: "the diagnostic must name the pass");
        result.Error.Message.Should().Contain("NotImplementedException",
            because: "the diagnostic must explain that FNA throws on it at runtime");
    }

    [Fact]
    public void Build_PassWithMultipleFnaThrowingStates_NamesAllOfThem()
    {
        var result = Build(
            ctabs: [],
            techniques:
            [
                new Fx2TechniqueSource("T",
                [
                    new Fx2PassSource("P0", -1, 0, new RenderStateBlock
                    {
                        KnownFnaThrowingStates = ["AlphaTestEnable", "FogEnable", "PointSpriteEnable"],
                    }),
                ]),
            ]);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().ContainAll("AlphaTestEnable", "FogEnable", "PointSpriteEnable");
    }

    [Fact]
    public void Build_AlphaTestEnableParsedFromFxSyntax_FailsWithSD0303EndToEnd()
    {
        // The exact flow the FNA pipeline runs: RenderStateParser captures the throwing
        // key as metadata, and the builder turns it into the loud SD0303.
        var parsed = new RenderStateParser().Parse(
            new Dictionary<string, string> { ["AlphaTestEnable"] = "True" });
        parsed.IsSuccess.Should().BeTrue(
            because: "the parser records the key as metadata rather than failing");

        var result = Build(
            ctabs: [],
            techniques: [new Fx2TechniqueSource("T", [new Fx2PassSource("P0", -1, 0, parsed.Value)])]);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SD0303");
        result.Error.Message.Should().Contain("AlphaTestEnable");
    }

    // ---------------------------------------------------------------------------
    // 9. Technique/pass passthrough: names and shader indices flow unchanged;
    //    -1 (stage absent) stays -1.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_TechniquesAndPasses_NamesAndIndicesPassThrough()
    {
        var result = Build(
            ctabs: [],
            techniques:
            [
                new Fx2TechniqueSource("TechA",
                [
                    new Fx2PassSource("P0", 0, 1, new RenderStateBlock()),
                    new Fx2PassSource("P1", -1, 2, new RenderStateBlock()),
                ]),
                new Fx2TechniqueSource("TechB",
                [
                    new Fx2PassSource("Only", 3, -1, new RenderStateBlock()),
                ]),
            ]);

        result.IsSuccess.Should().BeTrue(because: result.IsFailure ? result.Error.Message : "input is valid");

        IReadOnlyList<Fx2Technique> techniques = result.Value.Techniques;
        techniques.Select(t => t.Name).Should().Equal("TechA", "TechB");

        techniques[0].Passes.Select(p => p.Name).Should().Equal("P0", "P1");
        techniques[0].Passes[0].VertexShaderIndex.Should().Be(0);
        techniques[0].Passes[0].PixelShaderIndex.Should().Be(1);
        techniques[0].Passes[1].VertexShaderIndex.Should().Be(-1, because: "-1 (stage absent) must survive");
        techniques[0].Passes[1].PixelShaderIndex.Should().Be(2);

        techniques[1].Passes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Name = "Only", VertexShaderIndex = 3, PixelShaderIndex = -1 });

        techniques[0].Passes[0].RenderStates.Should().BeEmpty(
            because: "an empty RenderStateBlock maps to an empty state list");
    }

    // ---------------------------------------------------------------------------
    // 10. End-to-end: Build output feeds Fx2EffectWriter unchanged. The shader is a
    //     hand-assembled, structurally valid ps_2_0 token stream with a CTAB
    //     (docs/fx2-binary-format.md §11), round-tripped through CtabReader so the
    //     builder consumes the exact reflection the real pipeline would.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_ThenFx2EffectWriterWrite_SucceedsEndToEnd()
    {
        byte[] psBlob = BuildPs20BlobWithCtab(
        [
            Constant("Tint"),
            SamplerConstant("s0"),
        ]);

        // The synthetic blob must be valid per §11 — prove it with the project's own reader.
        var ctab = CtabReader.Read(psBlob, SourceFile);
        ctab.IsSuccess.Should().BeTrue(
            because: ctab.IsFailure ? ctab.Error.Message : "the hand-assembled CTAB must parse");
        ctab.Value.TargetProfile.Should().Be("ps_2_0");
        ctab.Value.Constants.Select(c => c.Name).Should().Equal("Tint", "s0");

        var built = Fx2EffectBuilder.Build(
            techniques:
            [
                new Fx2TechniqueSource("Main",
                [
                    new Fx2PassSource("P0", -1, 0, new RenderStateBlock
                    {
                        AlphaBlendEnable = true,
                        DepthBias = 0.25f,
                    }),
                ]),
            ],
            shaders: [new Fx2Shader(ShaderStage.Pixel, psBlob)],
            ctabs: [ctab.Value],
            samplerInfos: [Sampler("s0", "tex", ("MipFilter", "LINEAR"))],
            sourceFile: SourceFile);

        built.IsSuccess.Should().BeTrue(
            because: built.IsFailure ? built.Error.Message : "the builder input is valid");

        var written = new Fx2EffectWriter().Write(built.Value);
        written.IsSuccess.Should().BeTrue(
            because: written.IsFailure ? written.Error.Message : "the writer must accept the builder's output");
        written.Value.Should().NotBeEmpty();
        BitConverter.ToUInt32(written.Value, 0).Should().Be(0xFEFF0901,
            because: "the file must lead with the fx_2_0 version token");
    }

    /// <summary>
    /// Hand-assembles a minimal, structurally valid ps_2_0 token stream carrying a CTAB
    /// comment for <paramref name="constants"/> (docs/fx2-binary-format.md §11): version
    /// token 0xFFFF0200, one comment block (header, constant infos, type infos, strings),
    /// end token 0x0000FFFF. DefaultValue offsets are 0 (no defaults).
    /// </summary>
    private static byte[] BuildPs20BlobWithCtab(IReadOnlyList<CtabConstant> constants)
    {
        const uint psVersionToken = 0xFFFF0200;
        int count = constants.Count;
        int constantInfoOfs = 28;                    // header is 28 bytes
        int typeInfoOfs = constantInfoOfs + 20 * count;
        int stringsOfs = typeInfoOfs + 16 * count;   // strings after type infos (fxc layout)

        // Lay out the string table first so every offset is known up front.
        var stringOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        using var stringsMs = new MemoryStream();
        int AddString(string s)
        {
            if (stringOffsets.TryGetValue(s, out int existing))
                return existing;
            int ofs = stringsOfs + (int)stringsMs.Length;
            byte[] bytes = Encoding.ASCII.GetBytes(s);
            stringsMs.Write(bytes, 0, bytes.Length);
            stringsMs.WriteByte(0);
            stringOffsets[s] = ofs;
            return ofs;
        }

        int creatorOfs = AddString("ShadowDusk.Compiler.Tests");
        int targetOfs = AddString("ps_2_0");
        int[] nameOfs = constants.Select(c => AddString(c.Name)).ToArray();

        using var ctabMs = new MemoryStream();
        using var w = new BinaryWriter(ctabMs, Encoding.ASCII, leaveOpen: true);

        // D3DXSHADER_CONSTANTTABLE header (28 bytes, all u32).
        w.Write(28u);                  // Size
        w.Write((uint)creatorOfs);     // Creator
        w.Write(psVersionToken);       // Version — must equal the shader's version token
        w.Write((uint)count);          // Constants
        w.Write((uint)constantInfoOfs);// ConstantInfo
        w.Write(0u);                   // Flags — not read
        w.Write((uint)targetOfs);      // Target

        // D3DXSHADER_CONSTANTINFO records (20 bytes each).
        for (int i = 0; i < count; i++)
        {
            CtabConstant c = constants[i];
            w.Write((uint)nameOfs[i]);
            w.Write((ushort)c.RegisterSet);
            w.Write((ushort)c.RegisterIndex);
            w.Write((ushort)c.RegisterCount);
            w.Write((ushort)0);                      // Reserved
            w.Write((uint)(typeInfoOfs + 16 * i));   // TypeInfo
            w.Write(0u);                             // DefaultValue — 0 = none
        }

        // D3DXSHADER_TYPEINFO records (16 bytes each, u16 fields).
        foreach (CtabConstant c in constants)
        {
            w.Write((ushort)c.Class);
            w.Write((ushort)c.Type);
            w.Write((ushort)c.Rows);
            w.Write((ushort)c.Columns);
            w.Write((ushort)c.Elements);
            w.Write((ushort)0);  // StructMembers
            w.Write(0u);         // StructMemberInfo
        }

        // Strings, padded so the CTAB region is a whole number of dwords.
        w.Write(stringsMs.ToArray());
        while (ctabMs.Length % 4 != 0)
            w.Write((byte)0);
        w.Flush();
        byte[] ctabBytes = ctabMs.ToArray();

        // Shader container: version token, CTAB comment block, end token.
        using var blobMs = new MemoryStream();
        using var blob = new BinaryWriter(blobMs, Encoding.ASCII, leaveOpen: true);
        blob.Write(psVersionToken);
        uint commentDwords = (uint)(1 + ctabBytes.Length / 4); // 'CTAB' fourcc + region
        blob.Write((commentDwords << 16) | 0x0000FFFEu);       // bit 31 stays 0
        blob.Write(0x42415443u);                               // 'CTAB'
        blob.Write(ctabBytes);
        blob.Write(0x0000FFFFu);                               // end token
        blob.Flush();
        return blobMs.ToArray();
    }
}
