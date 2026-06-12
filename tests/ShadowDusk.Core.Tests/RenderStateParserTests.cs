#nullable enable

using System.Collections.Generic;
using FluentAssertions;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Core.Tests;

public sealed class RenderStateParserTests
{
    private static readonly RenderStateParser Parser = new();

    private static RenderStateBlock Parse(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) dict[k] = v;

        var result = Parser.Parse(dict);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    private static ShaderError ParseExpectError(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) dict[k] = v;

        var result = Parser.Parse(dict);
        result.IsFailure.Should().BeTrue();
        return result.Error;
    }

    // -------------------------------------------------------------------------
    // Empty / unknown keys
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_EmptyDictionary_ReturnsEmptyBlock()
    {
        var block = Parse();
        block.HasBlendState.Should().BeFalse();
        block.HasDepthStencilState.Should().BeFalse();
        block.HasRasterizerState.Should().BeFalse();
    }

    [Fact]
    public void Parse_UnknownKey_IsIgnored()
    {
        var block = Parse(("UnknownRenderKey", "SomeValue"));
        block.HasBlendState.Should().BeFalse();
        block.HasRasterizerState.Should().BeFalse();
        block.HasDepthStencilState.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // RasterizerState
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("None", CullModeValue.None)]
    [InlineData("CW",   CullModeValue.CullClockwiseFace)]
    [InlineData("CCW",  CullModeValue.CullCounterClockwiseFace)]
    public void Parse_CullMode(string value, CullModeValue expected)
    {
        var block = Parse(("CullMode", value));
        block.CullMode.Should().Be(expected);
    }

    [Fact]
    public void Parse_CullMode_CaseInsensitive()
    {
        var block = Parse(("cullmode", "none"));
        block.CullMode.Should().Be(CullModeValue.None);
    }

    [Fact]
    public void Parse_CullMode_InvalidValue_ReturnsError()
    {
        var error = ParseExpectError(("CullMode", "Backwards"));
        error.Code.Should().Be("SD0011");
        error.Message.Should().Contain("CullMode");
        error.Message.Should().Contain("Backwards");
    }

    [Theory]
    [InlineData("Solid",     FillModeValue.Solid)]
    [InlineData("Wireframe", FillModeValue.WireFrame)]
    public void Parse_FillMode(string value, FillModeValue expected)
    {
        var block = Parse(("FillMode", value));
        block.FillMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    [InlineData("true",  true)]
    [InlineData("false", false)]
    public void Parse_ScissorTestEnable(string value, bool expected)
    {
        var block = Parse(("ScissorTestEnable", value));
        block.ScissorTestEnable.Should().Be(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_MultiSampleAntiAlias(string value, bool expected)
    {
        var block = Parse(("MultiSampleAntiAlias", value));
        block.MultiSampleAntiAlias.Should().Be(expected);
    }

    [Fact]
    public void Parse_DepthBias()
    {
        var block = Parse(("DepthBias", "0.5"));
        block.DepthBias.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void Parse_DepthBias_Negative()
    {
        var block = Parse(("DepthBias", "-0.5"));
        block.DepthBias.Should().BeApproximately(-0.5f, 1e-6f);
    }

    [Fact]
    public void Parse_DepthBias_Exponent()
    {
        var block = Parse(("DepthBias", "1e-4"));
        block.DepthBias.Should().BeApproximately(1e-4f, 1e-9f);
    }

    [Fact]
    public void Parse_SlopeScaleDepthBias()
    {
        var block = Parse(("SlopeScaleDepthBias", "1.0"));
        block.SlopeScaleDepthBias.Should().BeApproximately(1.0f, 1e-6f);
    }

    // -------------------------------------------------------------------------
    // BlendState
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_AlphaBlendEnable(string value, bool expected)
    {
        var block = Parse(("AlphaBlendEnable", value));
        block.AlphaBlendEnable.Should().Be(expected);
    }

    [Theory]
    [InlineData("Zero",           BlendValue.Zero)]
    [InlineData("One",            BlendValue.One)]
    [InlineData("SrcColor",       BlendValue.SourceColor)]
    [InlineData("InvSrcColor",    BlendValue.InverseSourceColor)]
    [InlineData("SrcAlpha",       BlendValue.SourceAlpha)]
    [InlineData("InvSrcAlpha",    BlendValue.InverseSourceAlpha)]
    [InlineData("DestAlpha",      BlendValue.DestinationAlpha)]
    [InlineData("InvDestAlpha",   BlendValue.InverseDestinationAlpha)]
    [InlineData("DestColor",      BlendValue.DestinationColor)]
    [InlineData("InvDestColor",   BlendValue.InverseDestinationColor)]
    [InlineData("SrcAlphaSat",    BlendValue.SourceAlphaSaturation)]
    [InlineData("BlendFactor",    BlendValue.BlendFactor)]
    [InlineData("InvBlendFactor", BlendValue.InverseBlendFactor)]
    public void Parse_SrcBlend(string value, BlendValue expected)
    {
        var block = Parse(("SrcBlend", value));
        block.ColorSourceBlend.Should().Be(expected);
    }

    [Theory]
    [InlineData("Zero",        BlendValue.Zero)]
    [InlineData("One",         BlendValue.One)]
    [InlineData("InvSrcAlpha", BlendValue.InverseSourceAlpha)]
    public void Parse_DestBlend(string value, BlendValue expected)
    {
        var block = Parse(("DestBlend", value));
        block.ColorDestinationBlend.Should().Be(expected);
    }

    [Theory]
    [InlineData("Add",         BlendFunctionValue.Add)]
    [InlineData("Subtract",    BlendFunctionValue.Subtract)]
    [InlineData("RevSubtract", BlendFunctionValue.ReverseSubtract)]
    [InlineData("Min",         BlendFunctionValue.Min)]
    [InlineData("Max",         BlendFunctionValue.Max)]
    public void Parse_BlendOp(string value, BlendFunctionValue expected)
    {
        var block = Parse(("BlendOp", value));
        block.ColorBlendFunction.Should().Be(expected);
    }

    [Fact]
    public void Parse_SrcBlendAlpha()
    {
        var block = Parse(("SrcBlendAlpha", "SrcAlpha"));
        block.AlphaSourceBlend.Should().Be(BlendValue.SourceAlpha);
    }

    [Fact]
    public void Parse_DestBlendAlpha()
    {
        var block = Parse(("DestBlendAlpha", "InvSrcAlpha"));
        block.AlphaDestinationBlend.Should().Be(BlendValue.InverseSourceAlpha);
    }

    [Fact]
    public void Parse_BlendOpAlpha()
    {
        var block = Parse(("BlendOpAlpha", "Add"));
        block.AlphaBlendFunction.Should().Be(BlendFunctionValue.Add);
    }

    [Fact]
    public void Parse_ColorWriteEnable()
    {
        var block = Parse(("ColorWriteEnable", "15"));
        block.ColorWriteChannels.Should().Be(15);
    }

    [Fact]
    public void Parse_ColorWriteEnable_InvalidValue_ReturnsError()
    {
        var error = ParseExpectError(("ColorWriteEnable", "All"));
        error.Code.Should().Be("SD0011");
    }

    // -------------------------------------------------------------------------
    // DepthStencilState
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_ZEnable(string value, bool expected)
    {
        var block = Parse(("ZEnable", value));
        block.DepthBufferEnable.Should().Be(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_ZWriteEnable(string value, bool expected)
    {
        var block = Parse(("ZWriteEnable", value));
        block.DepthBufferWriteEnable.Should().Be(expected);
    }

    [Theory]
    [InlineData("Never",        CompareFunctionValue.Never)]
    [InlineData("Less",         CompareFunctionValue.Less)]
    [InlineData("Equal",        CompareFunctionValue.Equal)]
    [InlineData("LessEqual",    CompareFunctionValue.LessEqual)]
    [InlineData("Greater",      CompareFunctionValue.Greater)]
    [InlineData("NotEqual",     CompareFunctionValue.NotEqual)]
    [InlineData("GreaterEqual", CompareFunctionValue.GreaterEqual)]
    [InlineData("Always",       CompareFunctionValue.Always)]
    public void Parse_ZFunc(string value, CompareFunctionValue expected)
    {
        var block = Parse(("ZFunc", value));
        block.DepthBufferFunction.Should().Be(expected);
    }

    [Fact]
    public void Parse_ZFunc_InvalidValue_ReturnsError()
    {
        var error = ParseExpectError(("ZFunc", "Bogus"));
        error.Code.Should().Be("SD0011");
        error.Message.Should().Contain("ZFunc");
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_StencilEnable(string value, bool expected)
    {
        var block = Parse(("StencilEnable", value));
        block.StencilEnable.Should().Be(expected);
    }

    [Fact]
    public void Parse_StencilRef()
    {
        var block = Parse(("StencilRef", "128"));
        block.ReferenceStencil.Should().Be(128);
    }

    [Fact]
    public void Parse_StencilMask()
    {
        var block = Parse(("StencilMask", "255"));
        block.StencilMask.Should().Be(255);
    }

    [Fact]
    public void Parse_StencilWriteMask()
    {
        var block = Parse(("StencilWriteMask", "255"));
        block.StencilWriteMask.Should().Be(255);
    }

    [Theory]
    [InlineData("Keep",    StencilOperationValue.Keep)]
    [InlineData("Zero",    StencilOperationValue.Zero)]
    [InlineData("Replace", StencilOperationValue.Replace)]
    [InlineData("Incr",    StencilOperationValue.Increment)]
    [InlineData("Decr",    StencilOperationValue.Decrement)]
    [InlineData("IncrSat", StencilOperationValue.IncrementSaturation)]
    [InlineData("DecrSat", StencilOperationValue.DecrementSaturation)]
    [InlineData("Invert",  StencilOperationValue.Invert)]
    public void Parse_StencilFail(string value, StencilOperationValue expected)
    {
        var block = Parse(("StencilFail", value));
        block.StencilFail.Should().Be(expected);
    }

    [Fact]
    public void Parse_StencilZFail()
    {
        var block = Parse(("StencilZFail", "Replace"));
        block.StencilDepthBufferFail.Should().Be(StencilOperationValue.Replace);
    }

    [Fact]
    public void Parse_StencilPass()
    {
        var block = Parse(("StencilPass", "Keep"));
        block.StencilPass.Should().Be(StencilOperationValue.Keep);
    }

    [Theory]
    [InlineData("Never",        CompareFunctionValue.Never)]
    [InlineData("Always",       CompareFunctionValue.Always)]
    [InlineData("LessEqual",    CompareFunctionValue.LessEqual)]
    public void Parse_StencilFunc(string value, CompareFunctionValue expected)
    {
        var block = Parse(("StencilFunc", value));
        block.StencilFunction.Should().Be(expected);
    }

    // -------------------------------------------------------------------------
    // Multi-key round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_AllBlendFields_CorrectlyMapped()
    {
        var block = Parse(
            ("AlphaBlendEnable", "True"),
            ("SrcBlend",         "SrcAlpha"),
            ("DestBlend",        "InvSrcAlpha"),
            ("BlendOp",          "Add"),
            ("SrcBlendAlpha",    "One"),
            ("DestBlendAlpha",   "Zero"),
            ("BlendOpAlpha",     "Add"),
            ("ColorWriteEnable", "15"));

        block.AlphaBlendEnable.Should().BeTrue();
        block.ColorSourceBlend.Should().Be(BlendValue.SourceAlpha);
        block.ColorDestinationBlend.Should().Be(BlendValue.InverseSourceAlpha);
        block.ColorBlendFunction.Should().Be(BlendFunctionValue.Add);
        block.AlphaSourceBlend.Should().Be(BlendValue.One);
        block.AlphaDestinationBlend.Should().Be(BlendValue.Zero);
        block.AlphaBlendFunction.Should().Be(BlendFunctionValue.Add);
        block.ColorWriteChannels.Should().Be(15);
        block.HasBlendState.Should().BeTrue();
    }

    [Fact]
    public void Parse_HasRasterizerState_WhenCullModeSet()
    {
        var block = Parse(("CullMode", "CCW"));
        block.HasRasterizerState.Should().BeTrue();
        block.HasBlendState.Should().BeFalse();
        block.HasDepthStencilState.Should().BeFalse();
    }

    [Fact]
    public void Parse_HasDepthStencilState_WhenZEnableSet()
    {
        var block = Parse(("ZEnable", "True"));
        block.HasDepthStencilState.Should().BeTrue();
        block.HasBlendState.Should().BeFalse();
        block.HasRasterizerState.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // FNA-only states (fx_2_0 ops FNA honors; the MGFX writer never reads these)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("True",  true)]
    [InlineData("FALSE", false)]
    public void Parse_SeparateAlphaBlendEnable(string value, bool expected)
    {
        var block = Parse(("SeparateAlphaBlendEnable", value));
        block.SeparateAlphaBlendEnable.Should().Be(expected);
    }

    [Theory]
    [InlineData("0x80FF8080", 0x80FF8080u)] // hex D3DCOLOR dword
    [InlineData("0XFFFFFFFF", 0xFFFFFFFFu)] // upper-case prefix
    [InlineData("255",        255u)]        // decimal
    public void Parse_BlendFactor(string value, uint expected)
    {
        var block = Parse(("BlendFactor", value));
        block.BlendFactor.Should().Be(expected);
    }

    [Theory]
    [InlineData("NotAColor")]
    [InlineData("0x")]
    [InlineData("-1")]
    public void Parse_BlendFactor_InvalidValue_ReturnsError(string value)
    {
        var error = ParseExpectError(("BlendFactor", value));
        error.Code.Should().Be("SD0011");
        error.Message.Should().Contain("BlendFactor");
    }

    [Theory]
    [InlineData("0xFFFF0000", 0xFFFF0000u)]
    [InlineData("4294967295", 0xFFFFFFFFu)]
    public void Parse_MultiSampleMask(string value, uint expected)
    {
        var block = Parse(("MultiSampleMask", value));
        block.MultiSampleMask.Should().Be(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_TwoSidedStencilMode(string value, bool expected)
    {
        var block = Parse(("TwoSidedStencilMode", value));
        block.TwoSidedStencilMode.Should().Be(expected);
    }

    [Theory]
    [InlineData("Keep",    StencilOperationValue.Keep)]
    [InlineData("Zero",    StencilOperationValue.Zero)]
    [InlineData("Replace", StencilOperationValue.Replace)]
    [InlineData("Incr",    StencilOperationValue.Increment)]
    [InlineData("Decr",    StencilOperationValue.Decrement)]
    [InlineData("IncrSat", StencilOperationValue.IncrementSaturation)]
    [InlineData("DecrSat", StencilOperationValue.DecrementSaturation)]
    [InlineData("Invert",  StencilOperationValue.Invert)]
    public void Parse_CcwStencilFail(string value, StencilOperationValue expected)
    {
        var block = Parse(("CCW_StencilFail", value));
        block.CounterClockwiseStencilFail.Should().Be(expected);
    }

    [Fact]
    public void Parse_CcwStencilZFail()
    {
        var block = Parse(("CCW_StencilZFail", "Replace"));
        block.CounterClockwiseStencilDepthBufferFail.Should().Be(StencilOperationValue.Replace);
    }

    [Fact]
    public void Parse_CcwStencilPass()
    {
        var block = Parse(("CCW_StencilPass", "IncrSat"));
        block.CounterClockwiseStencilPass.Should().Be(StencilOperationValue.IncrementSaturation);
    }

    [Theory]
    [InlineData("ALWAYS",    CompareFunctionValue.Always)]
    [InlineData("Never",     CompareFunctionValue.Never)]
    [InlineData("LessEqual", CompareFunctionValue.LessEqual)]
    public void Parse_CcwStencilFunc(string value, CompareFunctionValue expected)
    {
        var block = Parse(("CCW_StencilFunc", value));
        block.CounterClockwiseStencilFunction.Should().Be(expected);
    }

    [Theory]
    [InlineData("RED | GREEN",              3)]  // flag-OR of D3DCOLORWRITEENABLE tokens
    [InlineData("Red|Green|Blue|Alpha",     15)] // no spaces, mixed case
    [InlineData("ALPHA",                    8)]  // single flag
    [InlineData("15",                       15)] // plain integer
    [InlineData("0x7",                      7)]  // hex integer
    [InlineData("RED | 0x2",                3)]  // flag and integer mixed
    public void Parse_ColorWriteEnable1(string value, int expected)
    {
        var block = Parse(("ColorWriteEnable1", value));
        block.ColorWriteChannels1.Should().Be(expected);
    }

    [Fact]
    public void Parse_ColorWriteEnable2()
    {
        var block = Parse(("ColorWriteEnable2", "RED | BLUE"));
        block.ColorWriteChannels2.Should().Be(5);
    }

    [Fact]
    public void Parse_ColorWriteEnable3()
    {
        var block = Parse(("ColorWriteEnable3", "GREEN"));
        block.ColorWriteChannels3.Should().Be(2);
    }

    [Theory]
    [InlineData("RED | PURPLE")]
    [InlineData("")]
    public void Parse_ColorWriteEnable1_InvalidValue_ReturnsError(string value)
    {
        var error = ParseExpectError(("ColorWriteEnable1", value));
        error.Code.Should().Be("SD0011");
        error.Message.Should().Contain("ColorWriteEnable1");
    }

    [Fact]
    public void Parse_FnaOnlyStates_DoNotFlipTheMgfxHasGates()
    {
        // The MGFX writer keys its three optional state-object headers off Has* —
        // the FNA-only fields must stay invisible to it (MGFX output non-regression).
        var block = Parse(
            ("SeparateAlphaBlendEnable", "True"),
            ("BlendFactor",              "0x80FF8080"),
            ("MultiSampleMask",          "0xFFFF0000"),
            ("TwoSidedStencilMode",      "True"),
            ("CCW_StencilFail",          "Keep"),
            ("CCW_StencilZFail",         "Decr"),
            ("CCW_StencilPass",          "Replace"),
            ("CCW_StencilFunc",          "Always"),
            ("ColorWriteEnable1",        "RED | GREEN"),
            ("ColorWriteEnable2",        "BLUE"),
            ("ColorWriteEnable3",        "0xF"));

        block.HasBlendState.Should().BeFalse();
        block.HasDepthStencilState.Should().BeFalse();
        block.HasRasterizerState.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Known-FNA-throwing keys (non-honored §8.2 ops): recorded as metadata, never
    // an error here — only the FNA path (Fx2EffectBuilder) fails on them.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("AlphaTestEnable",   "True")]
    [InlineData("AlphaFunc",         "Greater")]
    [InlineData("AlphaRef",          "128")]
    [InlineData("FogEnable",         "True")]
    [InlineData("FogColor",          "0xFFFFFFFF")]
    [InlineData("FogStart",          "10.0")]
    [InlineData("PointSpriteEnable", "True")]
    [InlineData("PointSize",         "4.0")]
    [InlineData("PointSize_Min",     "1.0")]
    [InlineData("Wrap0",             "1")]
    [InlineData("Lighting",          "False")]
    [InlineData("SRGBWriteEnable",   "True")]
    public void Parse_KnownFnaThrowingKey_IsRecordedNotErrored(string key, string value)
    {
        var block = Parse((key, value));
        block.KnownFnaThrowingStates.Should().ContainSingle().Which.Should().Be(key);
        block.HasBlendState.Should().BeFalse(because: "throwing keys map to no block field");
        block.HasDepthStencilState.Should().BeFalse();
        block.HasRasterizerState.Should().BeFalse();
    }

    [Fact]
    public void Parse_KnownFnaThrowingKey_ValueIsNotValidated()
    {
        // The key is the defect; the value never gets parsed (fxc would accept it and
        // FNA would throw at runtime regardless of the value).
        var block = Parse(("AlphaTestEnable", "garbage-value"));
        block.KnownFnaThrowingStates.Should().Equal("AlphaTestEnable");
    }

    [Fact]
    public void Parse_MultipleFnaThrowingKeys_SortedDeterministically()
    {
        var block = Parse(
            ("PointSpriteEnable", "True"),
            ("AlphaTestEnable",   "True"),
            ("FogEnable",         "True"));

        block.KnownFnaThrowingStates.Should().Equal("AlphaTestEnable", "FogEnable", "PointSpriteEnable");
    }

    [Fact]
    public void Parse_UnknownKey_IsNotRecordedAsFnaThrowing()
    {
        var block = Parse(("UnknownRenderKey", "SomeValue"));
        block.KnownFnaThrowingStates.Should().BeEmpty();
    }

    [Fact]
    public void Parse_HonoredKeys_AreNotRecordedAsFnaThrowing()
    {
        var block = Parse(("ZEnable", "True"), ("BlendFactor", "0x01020304"));
        block.KnownFnaThrowingStates.Should().BeEmpty();
    }
}
