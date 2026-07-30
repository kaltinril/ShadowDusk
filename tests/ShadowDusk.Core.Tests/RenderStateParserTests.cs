#nullable enable

using System.Collections.Generic;
using Shouldly;
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
        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private static ShaderError ParseExpectError(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) dict[k] = v;

        var result = Parser.Parse(dict);
        result.IsFailure.ShouldBeTrue();
        return result.Error;
    }

    // -------------------------------------------------------------------------
    // Empty / unknown keys
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_EmptyDictionary_ReturnsEmptyBlock()
    {
        var block = Parse();
        block.HasBlendState.ShouldBeFalse();
        block.HasDepthStencilState.ShouldBeFalse();
        block.HasRasterizerState.ShouldBeFalse();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Parse_BoolState_AcceptsNumericForms(string value, bool expected)
    {
        // mgfxc parity (bug-hunt 2026-07-27 M14): MonoGame's ParseTreeTools.ParseBool
        // accepts 1/0, and XNA-era effects write numeric bools (`AlphaBlendEnable = 1;`)
        // ubiquitously — these used to fail the whole effect with SD0011.
        var block = Parse(("StencilEnable", value));
        block.StencilEnable.ShouldBe(expected);
    }

    [Fact]
    public void Parse_StencilValues_AcceptHexForms()
    {
        // fxc accepts hex wherever an integer state value is legal, and real-world
        // stencil masks are routinely written 0xFF (bug-hunt 2026-07-27 M14).
        var block = Parse(
            ("StencilMask", "0xFF"),
            ("StencilWriteMask", "0x0F"),
            ("StencilRef", "0x10"));
        block.StencilMask.ShouldBe(0xFF);
        block.StencilWriteMask.ShouldBe(0x0F);
        block.ReferenceStencil.ShouldBe(0x10);
    }

    [Fact]
    public void Parse_UnknownKey_IsIgnored()
    {
        var block = Parse(("UnknownRenderKey", "SomeValue"));
        block.HasBlendState.ShouldBeFalse();
        block.HasRasterizerState.ShouldBeFalse();
        block.HasDepthStencilState.ShouldBeFalse();
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
        block.CullMode.ShouldBe(expected);
    }

    [Fact]
    public void Parse_CullMode_CaseInsensitive()
    {
        var block = Parse(("cullmode", "none"));
        block.CullMode.ShouldBe(CullModeValue.None);
    }

    [Fact]
    public void Parse_CullMode_InvalidValue_ReturnsError()
    {
        var error = ParseExpectError(("CullMode", "Backwards"));
        error.Code.ShouldBe("SD0011");
        error.Message.ShouldContain("CullMode", Case.Sensitive);
        error.Message.ShouldContain("Backwards", Case.Sensitive);
    }

    [Theory]
    [InlineData("Solid",     FillModeValue.Solid)]
    [InlineData("Wireframe", FillModeValue.WireFrame)]
    public void Parse_FillMode(string value, FillModeValue expected)
    {
        var block = Parse(("FillMode", value));
        block.FillMode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    [InlineData("true",  true)]
    [InlineData("false", false)]
    public void Parse_ScissorTestEnable(string value, bool expected)
    {
        var block = Parse(("ScissorTestEnable", value));
        block.ScissorTestEnable.ShouldBe(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_MultiSampleAntiAlias(string value, bool expected)
    {
        var block = Parse(("MultiSampleAntiAlias", value));
        block.MultiSampleAntiAlias.ShouldBe(expected);
    }

    [Fact]
    public void Parse_DepthBias()
    {
        var block = Parse(("DepthBias", "0.5"));
        block.DepthBias!.Value.ShouldBe(0.5f, 1e-6);
    }

    [Fact]
    public void Parse_DepthBias_Negative()
    {
        var block = Parse(("DepthBias", "-0.5"));
        block.DepthBias!.Value.ShouldBe(-0.5f, 1e-6);
    }

    [Fact]
    public void Parse_DepthBias_Exponent()
    {
        var block = Parse(("DepthBias", "1e-4"));
        block.DepthBias!.Value.ShouldBe(1e-4f, 1e-9);
    }

    [Fact]
    public void Parse_SlopeScaleDepthBias()
    {
        var block = Parse(("SlopeScaleDepthBias", "1.0"));
        block.SlopeScaleDepthBias!.Value.ShouldBe(1.0f, 1e-6);
    }

    [Theory]
    [InlineData("0.0001f", 0.0001f)]
    [InlineData("0.5F",    0.5f)]
    [InlineData("-2.0f",  -2.0f)]
    public void Parse_DepthBias_AcceptsHlslFloatSuffix(string value, float expected)
    {
        // The FxLexer deliberately keeps the HLSL `f`/`F` suffix inside the Number token,
        // and mgfxc's ParseTreeTools.ParseFloat strips it. A raw float.TryParse rejected
        // it, so `pass P { DepthBias = 0.0001f; }` — ordinary HLSL that mgfxc compiles —
        // failed SD0011 and aborted the whole compile.
        Parse(("DepthBias", value)).DepthBias!.Value.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Parse_SlopeScaleDepthBias_AcceptsHlslFloatSuffix()
        => Parse(("SlopeScaleDepthBias", "2.0f")).SlopeScaleDepthBias!.Value
               .ShouldBe(2.0f, 1e-6);

    // -------------------------------------------------------------------------
    // BlendState
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_AlphaBlendEnable(string value, bool expected)
    {
        var block = Parse(("AlphaBlendEnable", value));
        block.AlphaBlendEnable.ShouldBe(expected);
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
        block.ColorSourceBlend.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Zero",        BlendValue.Zero)]
    [InlineData("One",         BlendValue.One)]
    [InlineData("InvSrcAlpha", BlendValue.InverseSourceAlpha)]
    public void Parse_DestBlend(string value, BlendValue expected)
    {
        var block = Parse(("DestBlend", value));
        block.ColorDestinationBlend.ShouldBe(expected);
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
        block.ColorBlendFunction.ShouldBe(expected);
    }

    [Fact]
    public void Parse_SrcBlendAlpha()
    {
        var block = Parse(("SrcBlendAlpha", "SrcAlpha"));
        block.AlphaSourceBlend.ShouldBe(BlendValue.SourceAlpha);
    }

    [Fact]
    public void Parse_DestBlendAlpha()
    {
        var block = Parse(("DestBlendAlpha", "InvSrcAlpha"));
        block.AlphaDestinationBlend.ShouldBe(BlendValue.InverseSourceAlpha);
    }

    [Fact]
    public void Parse_BlendOpAlpha()
    {
        var block = Parse(("BlendOpAlpha", "Add"));
        block.AlphaBlendFunction.ShouldBe(BlendFunctionValue.Add);
    }

    // Phase 45 B3: the bare ColorWriteEnable key now uses TryParseColorWriteMask
    // (like ColorWriteEnable1/2/3), so symbolic single AND OR'd masks resolve in
    // addition to the numeric forms. (It previously used int.TryParse, which
    // rejected every symbolic flag — an unintended asymmetry vs. the numbered keys.)
    [Theory]
    [InlineData("15",                       15)] // plain integer
    [InlineData("0x0F",                     15)] // hex integer
    [InlineData("0",                        0)]  // explicit "write nothing"
    [InlineData("Red",                      1)]  // single symbolic flag
    [InlineData("Red | Green | Blue",       7)]  // OR'd symbolic flags
    [InlineData("All",                      15)] // the ALL alias (RED|GREEN|BLUE|ALPHA)
    [InlineData("RED | 0x8",                9)]  // flag + integer mixed
    // mgfxc's grammar admits `None` and a Boolean wherever a colour flag is legal
    // (ParseNode.EvalColors_None / EvalColors_Boolean). `ColorWriteEnable = None;` is the
    // idiomatic depth-only / stencil-only pass; it used to hard-fail SD0011 here while
    // mgfxc compiled the same .fx.
    [InlineData("None",                     0)]
    [InlineData("none",                     0)]
    [InlineData("false",                    0)]
    [InlineData("true",                     15)]
    [InlineData("Red | None",               1)]  // None is OR-identity inside a mask
    public void Parse_ColorWriteEnable(string value, int expected)
    {
        var block = Parse(("ColorWriteEnable", value));
        block.ColorWriteChannels.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Purple")]         // unknown flag name
    [InlineData("Red | Magenta")]  // unknown flag inside an OR
    public void Parse_ColorWriteEnable_InvalidValue_ReturnsError(string value)
    {
        var error = ParseExpectError(("ColorWriteEnable", value));
        error.Code.ShouldBe("SD0011");
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
        block.DepthBufferEnable.ShouldBe(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_ZWriteEnable(string value, bool expected)
    {
        var block = Parse(("ZWriteEnable", value));
        block.DepthBufferWriteEnable.ShouldBe(expected);
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
        block.DepthBufferFunction.ShouldBe(expected);
    }

    [Fact]
    public void Parse_ZFunc_InvalidValue_ReturnsError()
    {
        var error = ParseExpectError(("ZFunc", "Bogus"));
        error.Code.ShouldBe("SD0011");
        error.Message.ShouldContain("ZFunc", Case.Sensitive);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_StencilEnable(string value, bool expected)
    {
        var block = Parse(("StencilEnable", value));
        block.StencilEnable.ShouldBe(expected);
    }

    [Fact]
    public void Parse_StencilRef()
    {
        var block = Parse(("StencilRef", "128"));
        block.ReferenceStencil.ShouldBe(128);
    }

    [Fact]
    public void Parse_StencilMask()
    {
        var block = Parse(("StencilMask", "255"));
        block.StencilMask.ShouldBe(255);
    }

    [Fact]
    public void Parse_StencilWriteMask()
    {
        var block = Parse(("StencilWriteMask", "255"));
        block.StencilWriteMask.ShouldBe(255);
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
        block.StencilFail.ShouldBe(expected);
    }

    [Fact]
    public void Parse_StencilZFail()
    {
        var block = Parse(("StencilZFail", "Replace"));
        block.StencilDepthBufferFail.ShouldBe(StencilOperationValue.Replace);
    }

    [Fact]
    public void Parse_StencilPass()
    {
        var block = Parse(("StencilPass", "Keep"));
        block.StencilPass.ShouldBe(StencilOperationValue.Keep);
    }

    [Theory]
    [InlineData("Never",        CompareFunctionValue.Never)]
    [InlineData("Always",       CompareFunctionValue.Always)]
    [InlineData("LessEqual",    CompareFunctionValue.LessEqual)]
    public void Parse_StencilFunc(string value, CompareFunctionValue expected)
    {
        var block = Parse(("StencilFunc", value));
        block.StencilFunction.ShouldBe(expected);
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

        block.AlphaBlendEnable.ShouldBe(true);
        block.ColorSourceBlend.ShouldBe(BlendValue.SourceAlpha);
        block.ColorDestinationBlend.ShouldBe(BlendValue.InverseSourceAlpha);
        block.ColorBlendFunction.ShouldBe(BlendFunctionValue.Add);
        block.AlphaSourceBlend.ShouldBe(BlendValue.One);
        block.AlphaDestinationBlend.ShouldBe(BlendValue.Zero);
        block.AlphaBlendFunction.ShouldBe(BlendFunctionValue.Add);
        block.ColorWriteChannels.ShouldBe(15);
        block.HasBlendState.ShouldBeTrue();
    }

    [Fact]
    public void Parse_HasRasterizerState_WhenCullModeSet()
    {
        var block = Parse(("CullMode", "CCW"));
        block.HasRasterizerState.ShouldBeTrue();
        block.HasBlendState.ShouldBeFalse();
        block.HasDepthStencilState.ShouldBeFalse();
    }

    [Fact]
    public void Parse_HasDepthStencilState_WhenZEnableSet()
    {
        var block = Parse(("ZEnable", "True"));
        block.HasDepthStencilState.ShouldBeTrue();
        block.HasBlendState.ShouldBeFalse();
        block.HasRasterizerState.ShouldBeFalse();
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
        block.SeparateAlphaBlendEnable.ShouldBe(expected);
    }

    [Theory]
    [InlineData("0x80FF8080", 0x80FF8080u)] // hex D3DCOLOR dword
    [InlineData("0XFFFFFFFF", 0xFFFFFFFFu)] // upper-case prefix
    [InlineData("255",        255u)]        // decimal
    public void Parse_BlendFactor(string value, uint expected)
    {
        var block = Parse(("BlendFactor", value));
        block.BlendFactor.ShouldBe(expected);
    }

    [Theory]
    [InlineData("NotAColor")]
    [InlineData("0x")]
    [InlineData("-1")]
    public void Parse_BlendFactor_InvalidValue_ReturnsError(string value)
    {
        var error = ParseExpectError(("BlendFactor", value));
        error.Code.ShouldBe("SD0011");
        error.Message.ShouldContain("BlendFactor", Case.Sensitive);
    }

    [Theory]
    [InlineData("0xFFFF0000", 0xFFFF0000u)]
    [InlineData("4294967295", 0xFFFFFFFFu)]
    public void Parse_MultiSampleMask(string value, uint expected)
    {
        var block = Parse(("MultiSampleMask", value));
        block.MultiSampleMask.ShouldBe(expected);
    }

    [Theory]
    [InlineData("True",  true)]
    [InlineData("False", false)]
    public void Parse_TwoSidedStencilMode(string value, bool expected)
    {
        var block = Parse(("TwoSidedStencilMode", value));
        block.TwoSidedStencilMode.ShouldBe(expected);
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
        block.CounterClockwiseStencilFail.ShouldBe(expected);
    }

    [Fact]
    public void Parse_CcwStencilZFail()
    {
        var block = Parse(("CCW_StencilZFail", "Replace"));
        block.CounterClockwiseStencilDepthBufferFail.ShouldBe(StencilOperationValue.Replace);
    }

    [Fact]
    public void Parse_CcwStencilPass()
    {
        var block = Parse(("CCW_StencilPass", "IncrSat"));
        block.CounterClockwiseStencilPass.ShouldBe(StencilOperationValue.IncrementSaturation);
    }

    [Theory]
    [InlineData("ALWAYS",    CompareFunctionValue.Always)]
    [InlineData("Never",     CompareFunctionValue.Never)]
    [InlineData("LessEqual", CompareFunctionValue.LessEqual)]
    public void Parse_CcwStencilFunc(string value, CompareFunctionValue expected)
    {
        var block = Parse(("CCW_StencilFunc", value));
        block.CounterClockwiseStencilFunction.ShouldBe(expected);
    }

    [Theory]
    [InlineData("RED | GREEN",              3)]  // flag-OR of D3DCOLORWRITEENABLE tokens
    [InlineData("Red|Green|Blue|Alpha",     15)] // no spaces, mixed case
    [InlineData("ALPHA",                    8)]  // single flag
    [InlineData("All",                      15)] // the ALL alias (Phase 45 B3)
    [InlineData("15",                       15)] // plain integer
    [InlineData("0x7",                      7)]  // hex integer
    [InlineData("RED | 0x2",                3)]  // flag and integer mixed
    public void Parse_ColorWriteEnable1(string value, int expected)
    {
        var block = Parse(("ColorWriteEnable1", value));
        block.ColorWriteChannels1.ShouldBe(expected);
    }

    [Fact]
    public void Parse_ColorWriteEnable2()
    {
        var block = Parse(("ColorWriteEnable2", "RED | BLUE"));
        block.ColorWriteChannels2.ShouldBe(5);
    }

    [Fact]
    public void Parse_ColorWriteEnable3()
    {
        var block = Parse(("ColorWriteEnable3", "GREEN"));
        block.ColorWriteChannels3.ShouldBe(2);
    }

    [Theory]
    [InlineData("RED | PURPLE")]
    [InlineData("")]
    public void Parse_ColorWriteEnable1_InvalidValue_ReturnsError(string value)
    {
        var error = ParseExpectError(("ColorWriteEnable1", value));
        error.Code.ShouldBe("SD0011");
        error.Message.ShouldContain("ColorWriteEnable1", Case.Sensitive);
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

        block.HasBlendState.ShouldBeFalse();
        block.HasDepthStencilState.ShouldBeFalse();
        block.HasRasterizerState.ShouldBeFalse();
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
        block.KnownFnaThrowingStates.ShouldHaveSingleItem().ShouldBe(key);
        block.HasBlendState.ShouldBeFalse("throwing keys map to no block field");
        block.HasDepthStencilState.ShouldBeFalse();
        block.HasRasterizerState.ShouldBeFalse();
    }

    [Fact]
    public void Parse_KnownFnaThrowingKey_ValueIsNotValidated()
    {
        // The key is the defect; the value never gets parsed (fxc would accept it and
        // FNA would throw at runtime regardless of the value).
        var block = Parse(("AlphaTestEnable", "garbage-value"));
        block.KnownFnaThrowingStates.ShouldBe(new[] { "AlphaTestEnable" });
    }

    [Fact]
    public void Parse_MultipleFnaThrowingKeys_SortedDeterministically()
    {
        var block = Parse(
            ("PointSpriteEnable", "True"),
            ("AlphaTestEnable",   "True"),
            ("FogEnable",         "True"));

        block.KnownFnaThrowingStates.ShouldBe(new[] {"AlphaTestEnable", "FogEnable", "PointSpriteEnable"});
    }

    [Fact]
    public void Parse_UnknownKey_IsNotRecordedAsFnaThrowing()
    {
        var block = Parse(("UnknownRenderKey", "SomeValue"));
        block.KnownFnaThrowingStates.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_HonoredKeys_AreNotRecordedAsFnaThrowing()
    {
        var block = Parse(("ZEnable", "True"), ("BlendFactor", "0x01020304"));
        block.KnownFnaThrowingStates.ShouldBeEmpty();
    }
}
