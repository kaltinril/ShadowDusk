#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.HLSL;
using ShadowDusk.HLSL.Ast;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// Issue #299: the register an explicit <c>register(sN)</c> on a LEGACY <c>sampler</c>
/// declaration pins its texture to is decided on the PREPROCESSED source, like <c>mgfxc</c>
/// 3.8.4.1, not on the raw tokens. Measured against the pinned mgfxc <c>/Profile:OpenGL</c>
/// (2026-10-02), one sampler read by <c>tex2D</c>:
/// <list type="bullet">
///   <item><description><c>register(s1)</c> only in the inactive <c>#else</c> branch, for
///   <c>sampler</c>/<c>sampler2D</c> with a <c>sampler_state</c> block, the bare form and the
///   brace form: <c>ps_s0</c> (the raw reading said <c>ps_s1</c>).</description></item>
///   <item><description><c>register(s2)</c> in the active branch and <c>register(s1)</c> in the
///   inactive one: <c>ps_s2</c> (raw: <c>ps_s1</c>).</description></item>
///   <item><description><c>#define REG s1</c> / <c>register(REG)</c>, all four forms:
///   <c>ps_s1</c> (raw: <c>ps_s0</c>).</description></item>
/// </list>
/// These tests are pure: the source is handed over already flattened, with the OpenGL macro
/// prepend the compile uses, next to the raw parse the compile feeds from.
/// </summary>
public sealed class GlLegacySamplerRegisterPreprocessedTests
{
    private const string Ps = """

        float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
        technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
        """;

    private static FxParseResult RawParse(string raw)
    {
        var parsed = FxPreParser.Parse(raw, "t.fx");
        parsed.IsSuccess.ShouldBeTrue(parsed.IsFailure ? parsed.Error.Message : "ok");
        return parsed.Value;
    }

    private static string Gl(string raw, params UserDefine[] userDefines)
    {
        MacroSet macros = PlatformMacros.For(PlatformTarget.OpenGL) with { UserDefines = userDefines };
        return macros.ToTextPrepend("t.fx") + raw;
    }

    /// <summary>The preprocessed reading for an OpenGL compile of <paramref name="raw"/>.</summary>
    private static GlSamplerSlots Slots(string raw, params UserDefine[] userDefines)
    {
        var result = FxPreParser.CollectGlSamplerSlots(Gl(raw, userDefines), "t.fx", RawParse(raw));
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : "ok");
        return result.Value;
    }

    private static Dictionary<string, int> Map(params (string Texture, int Slot)[] entries) =>
        entries.ToDictionary(e => e.Texture, e => e.Slot, StringComparer.Ordinal);

    [Fact]
    public void RegisterOnlyInTheInactiveBranch_SamplerStateForm_PinsNothing()
    {
        string raw = """
            Texture2D Tex;
            #if OPENGL
            sampler S = sampler_state { Texture = <Tex>; };
            #else
            sampler S : register(s1) = sampler_state { Texture = <Tex>; };
            #endif
            """ + Ps;

        // The raw-token reading (the pre-fix behaviour) counts the dead branch.
        RawParse(raw).ExplicitGlSamplerSlots.ShouldBe(Map(("Tex", 1)));
        Slots(raw).Explicit.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sampler")]
    [InlineData("sampler2D")]
    public void RegisterOnlyInTheInactiveBranch_BareForm_PinsNothing(string keyword)
    {
        string raw = $"""
            #if OPENGL
            {keyword} S;
            #else
            {keyword} S : register(s1);
            #endif
            """ + Ps;

        RawParse(raw).ExplicitGlSamplerSlots.ShouldBe(Map(("S_SDTexture", 1)));
        Slots(raw).Explicit.ShouldBeEmpty();
    }

    [Fact]
    public void RegisterOnlyInTheInactiveBranch_BraceForm_PinsNothing()
    {
        string raw = """
            Texture2D Tex;
            #if OPENGL
            sampler S { Texture = <Tex>; };
            #else
            sampler S : register(s1) { Texture = <Tex>; };
            #endif
            """ + Ps;

        RawParse(raw).ExplicitGlSamplerSlots.ShouldBe(Map(("Tex", 1)));
        Slots(raw).Explicit.ShouldBeEmpty();
    }

    [Fact]
    public void RegisterOnlyInTheActiveBranch_IsPinned()
    {
        string raw = """
            Texture2D Tex;
            #if OPENGL
            sampler2D S : register(s1) = sampler_state { Texture = <Tex>; };
            #else
            sampler2D S = sampler_state { Texture = <Tex>; };
            #endif
            """ + Ps;

        Slots(raw).Explicit.ShouldBe(Map(("Tex", 1)));
    }

    [Fact]
    public void ADifferentRegisterInEachBranch_TakesTheActiveBranchs()
    {
        string raw = """
            #if SM6
            sampler S : register(s3);
            #elif OPENGL
            sampler S : register(s2);
            #else
            sampler S : register(s1);
            #endif
            """ + Ps;

        // Raw: the last declaration in the file wins, whichever branch it is in.
        RawParse(raw).ExplicitGlSamplerSlots.ShouldBe(Map(("S_SDTexture", 1)));
        Slots(raw).Explicit.ShouldBe(Map(("S_SDTexture", 2)));
    }

    [Theory]
    [InlineData("sampler S : register(REG);", "S_SDTexture")]
    [InlineData("sampler2D S : register(REG);", "S_SDTexture")]
    [InlineData("Texture2D Tex;\nsampler S : register(REG) = sampler_state { Texture = <Tex>; };", "Tex")]
    [InlineData("Texture2D Tex;\nsampler2D S : register(REG) = sampler_state { Texture = <Tex>; };", "Tex")]
    [InlineData("Texture2D Tex;\nsampler S : register(REG) { Texture = <Tex>; };", "Tex")]
    public void RegisterNumberSpelledThroughAMacro_IsPinned(string declaration, string texture)
    {
        string raw = "#define REG s1\n" + declaration + Ps;

        // Raw: `register(REG)` is not a register number, so nothing is recorded.
        RawParse(raw).ExplicitGlSamplerSlots.ShouldBeEmpty();
        Slots(raw).Explicit.ShouldBe(Map((texture, 1)));
    }

    [Fact]
    public void RegisterNumberFromAUserDefine_IsPinned()
    {
        string raw = "sampler S : register(SAMPLER_REG);" + Ps;

        Slots(raw).Explicit.ShouldBeEmpty();
        Slots(raw, new UserDefine("SAMPLER_REG", "s4")).Explicit.ShouldBe(Map(("S_SDTexture", 4)));
    }

    [Fact]
    public void RegisterGatedOnAUserDefine_FollowsTheDefine()
    {
        string raw = """
            Texture2D Tex;
            #if PIN_SAMPLER
            sampler S : register(s1) = sampler_state { Texture = <Tex>; };
            #else
            sampler S = sampler_state { Texture = <Tex>; };
            #endif
            """ + Ps;

        Slots(raw).Explicit.ShouldBeEmpty();
        Slots(raw, new UserDefine("PIN_SAMPLER")).Explicit.ShouldBe(Map(("Tex", 1)));
    }

    [Fact]
    public void MixedSet_PinsOnlyTheAnnotatedSamplers()
    {
        // The OpenGL arm of apos-shapes-sm6.fx: explicit s0, an unannotated sampler, explicit s2.
        string raw = """
            sampler TextureSampler : register(s0);
            sampler FontSampler;
            sampler BlueNoiseSampler : register(s2);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(TextureSampler, uv) + tex2D(FontSampler, uv) + tex2D(BlueNoiseSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        Slots(raw).Explicit.ShouldBe(Map(("TextureSampler_SDTexture", 0), ("BlueNoiseSampler_SDTexture", 2)));
    }

    [Fact]
    public void ModernRegisterInTheDeadSm6Arm_NoLongerPinsTheLegacyArmsTexture()
    {
        // VsTransformColorTexture.fx / VsWaveQuadIntrinsics.fx: the SM6 arm writes the sampler as
        // a modern SamplerState with a register, the OpenGL arm declares the same NAME through the
        // legacy syntax. The raw reading mapped the SM6 arm's register onto the legacy arm's
        // texture. mgfxc sees only the OpenGL arm: nothing pinned, nothing reserved, and the
        // single pair takes register 0 by declaration order, which is the same answer.
        string raw = """
            #if SM6
            Texture2D    SpriteTexture        : register(t0);
            SamplerState SpriteTextureSampler : register(s0);
            #else
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };
            #endif
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
            #if SM6
                return SpriteTexture.Sample(SpriteTextureSampler, uv);
            #else
                return tex2D(SpriteTextureSampler, uv);
            #endif
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        RawParse(raw).ExplicitGlSamplerSlots.ShouldBe(Map(("SpriteTexture", 0)));

        GlSamplerSlots slots = Slots(raw);
        slots.Explicit.ShouldBeEmpty();
        slots.Reserved.ShouldBeEmpty();
    }

    [Fact]
    public void ModernSamplerReadThroughSample_IsReservedNotPinned()
    {
        string raw = """
            Texture2D Tex;
            SamplerState S : register(s2);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return Tex.Sample(S, uv); }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        GlSamplerSlots slots = Slots(raw);
        slots.Explicit.ShouldBeEmpty();
        slots.Reserved.ShouldBe(new[] { 2 });
    }

    [Fact]
    public void RegisterInAComment_IsNotPinned()
    {
        string raw = """
            // sampler S : register(s3);
            sampler S /* : register(s1) */;
            """ + Ps;

        Slots(raw).Explicit.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sampler S : register(t1);")]
    [InlineData("sampler S : register(s);")]
    [InlineData("sampler S : register(s256);")]
    [InlineData("sampler S : register(ps, s1);")]
    public void ClauseThatIsNotAPlainSamplerRegister_IsNotPinned(string declaration)
    {
        // The same narrow shape the main parse records: only `register ( sN )`, N in a byte.
        string raw = declaration + Ps;

        RawParse(raw).ExplicitGlSamplerSlots.ShouldBeEmpty();
        Slots(raw).Explicit.ShouldBeEmpty();
    }

    [Fact]
    public void WithNoConditionalsOrMacros_AgreesWithTheRawReading()
    {
        string raw = """
            Texture2D SpriteTexture;
            sampler SpriteSampler : register(s0) = sampler_state { Texture = <SpriteTexture>; };
            sampler MaskA : register(s2);
            sampler2D MaskB : register(s3);
            SamplerState Unused : register(s5);
            float4 PS(float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(SpriteSampler, uv) + tex2D(MaskA, uv) + tex2D(MaskB, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 PS(); } }
            """;

        FxParseResult parsed = RawParse(raw);
        GlSamplerSlots slots = Slots(raw);

        slots.Explicit.OrderBy(e => e.Key, StringComparer.Ordinal)
            .ShouldBe(parsed.ExplicitGlSamplerSlots.OrderBy(e => e.Key, StringComparer.Ordinal));
        slots.Explicit.ShouldBe(
            Map(("SpriteTexture", 0), ("MaskA_SDTexture", 2), ("MaskB_SDTexture", 3)), ignoreOrder: true);
        slots.Reserved.ShouldBe(parsed.ReservedGlSamplerSlots, ignoreOrder: true);
    }

    [Fact]
    public void ReservedHalf_IsTheIssue283Reading()
    {
        string raw = """
            #define SLOT(n) : register(n)
            Texture2D MaskA;
            SamplerState SampA SLOT(s0);
            #if OPENGL
            SamplerState SampB;
            #else
            SamplerState SampB : register(s1);
            #endif
            """;

        Slots(raw).Reserved.ShouldBe(FxPreParser.CollectReservedGlSamplerSlots(Gl(raw), "t.fx").Value);
        Slots(raw).Reserved.ShouldBe(new[] { 0 });
    }

    [Fact]
    public void ViewThatCannotBeBuilt_IsSd0009()
    {
        string raw = """
            #if 1 +
            #endif
            sampler S : register(s1);
            """ + Ps;

        var result = FxPreParser.CollectGlSamplerSlots(Gl(raw), "t.fx", RawParse(raw));

        result.IsFailure.ShouldBeTrue("the preprocessed view should not have been buildable");
        result.Error.Code.ShouldBe("SD0009");
    }
}
