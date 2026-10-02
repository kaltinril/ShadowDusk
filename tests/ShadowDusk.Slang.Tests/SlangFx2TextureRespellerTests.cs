#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>Pure unit tests for <see cref="SlangFx2TextureRespeller"/> (issue #230).</summary>
public sealed class SlangFx2TextureRespellerTests
{
    private const string SlangcEmission = """
        Texture2D<float4 > SpriteTexture;
        SamplerState SpriteSampler;

        float4 MainPS(float4 pos_0 : SV_Position, float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            float4 c_0 = SpriteTexture.Sample(SpriteSampler, uv_0);
            return float4(1.0f - c_0.xyz, c_0.w);
        }
        """;

    private static string Respell(string hlsl)
    {
        string? result = SlangFx2TextureRespeller.TryRespell(hlsl, out string? unsupported);
        result.ShouldNotBeNull(unsupported);
        unsupported.ShouldBeNull();
        return result;
    }

    private static string Rejected(string hlsl)
    {
        SlangFx2TextureRespeller.TryRespell(hlsl, out string? unsupported).ShouldBeNull();
        unsupported.ShouldNotBeNull();
        return unsupported;
    }

    // In multiline mode .NET's '$' matches only before '\n', so a CRLF line once made every
    // declaration unmatchable (red on windows-latest CI, whose checkout has CRLF sources).
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Declarations_AreRespelled_UnderEitherLineEnding(string newline)
    {
        string fx = Respell(SlangcEmission.ReplaceLineEndings(newline));

        fx.ShouldContain("texture2D SpriteTexture;" + newline, Case.Sensitive);
        fx.ShouldContain("sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };" + newline, Case.Sensitive);
        fx.ShouldNotContain("Texture2D", Case.Sensitive);
        fx.ShouldNotContain("SamplerState", Case.Sensitive);
    }

    [Fact]
    public void TextureObjects_BecomeDx9EffectSyntax()
    {
        string fx = Respell(SlangcEmission);

        fx.ShouldContain("texture2D SpriteTexture;", Case.Sensitive);
        fx.ShouldContain("sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };", Case.Sensitive);
        fx.ShouldContain("float4 c_0 = tex2D(SpriteSampler, uv_0);", Case.Sensitive);
        fx.ShouldNotContain("Texture2D", Case.Sensitive);
        fx.ShouldNotContain("SamplerState", Case.Sensitive);
        fx.ShouldNotContain(".Sample", Case.Sensitive);
    }

    [Fact]
    public void EverythingElse_IsUntouched()
    {
        Respell(SlangcEmission).ShouldContain("return float4(1.0f - c_0.xyz, c_0.w);", Case.Sensitive);
    }

    [Fact]
    public void TextureFreeText_ComesBackUnchanged()
    {
        const string hlsl = "float4 MainPS(float2 uv : TEXCOORD0) : SV_TARGET { return float4(uv, 0, 1); }";
        Respell(hlsl).ShouldBe(hlsl);
    }

    [Fact]
    public void AuthorSamplerRegister_IsKept_TextureRegisterDropped()
    {
        string fx = Respell(SlangcEmission
            .Replace("SamplerState SpriteSampler;", "SamplerState SpriteSampler : register(s2);")
            .Replace("Texture2D<float4 > SpriteTexture;", "Texture2D<float4 > SpriteTexture : register(t2);"));

        fx.ShouldContain("sampler2D SpriteSampler : register(s2) = sampler_state { Texture = <SpriteTexture>; };", Case.Sensitive);
        fx.ShouldContain("texture2D SpriteTexture;", Case.Sensitive);
    }

    [Fact]
    public void NestedSample_InTheUvArgument_IsRespelled()
    {
        const string hlsl = """
            Texture2D<float4 > A;
            SamplerState SA;
            Texture2D<float4 > B;
            SamplerState SB;
            float4 F(float2 uv) { return A.Sample(SA, B.Sample(SB, uv).xy); }
            """;

        string fx = Respell(hlsl);

        fx.ShouldContain("return tex2D(SA, tex2D(SB, uv).xy);", Case.Sensitive);
        fx.ShouldContain("sampler2D SA = sampler_state { Texture = <A>; };", Case.Sensitive);
        fx.ShouldContain("sampler2D SB = sampler_state { Texture = <B>; };", Case.Sensitive);
    }

    [Fact]
    public void OneSamplerWithTwoTextures_IsRejected()
    {
        const string hlsl = """
            Texture2D<float4 > A;
            Texture2D<float4 > B;
            SamplerState S;
            float4 F(float2 uv) { return A.Sample(S, uv) + B.Sample(S, uv); }
            """;

        Rejected(hlsl).ShouldContain("two textures", Case.Sensitive);
    }

    [Theory]
    [InlineData("SpriteTexture.Sample(SpriteSampler, uv_0, int2(1, 0))", "3 arguments")]
    [InlineData("SpriteTexture.SampleLevel(SpriteSampler, uv_0, 0.0f)", "SampleLevel")]
    [InlineData("SpriteTexture.Load(int3(0, 0, 0))", "Load")]
    public void UnmodeledTextureCalls_AreRejectedByName(string call, string named)
    {
        Rejected(SlangcEmission.Replace("SpriteTexture.Sample(SpriteSampler, uv_0)", call))
            .ShouldContain(named, Case.Sensitive);
    }

    [Fact]
    public void SamplerEmittedBeforeItsTexture_TextureDeclarationComesFirst()
    {
        // slangc hoists a sampler out of a struct/ParameterBlock ahead of a global texture;
        // fxc rejects a sampler_state that names a texture declared after it.
        const string hlsl = """
            SamplerState gS_s_0;
            Texture2D<float4 > SpriteTexture;
            float4 F(float2 uv) { return SpriteTexture.Sample(gS_s_0, uv); }
            """;

        string fx = Respell(hlsl);

        int texture = fx.IndexOf("texture2D SpriteTexture;", StringComparison.Ordinal);
        int sampler = fx.IndexOf("sampler2D gS_s_0 = sampler_state { Texture = <SpriteTexture>; };", StringComparison.Ordinal);
        texture.ShouldBeGreaterThanOrEqualTo(0);
        sampler.ShouldBeGreaterThan(texture);
        fx.Split('\n').Length.ShouldBe(hlsl.Split('\n').Length, "line structure is preserved for #line mapping");
    }

    [Fact]
    public void SeveralTextures_AllDeclaredBeforeAnySampler()
    {
        const string hlsl = """
            Texture2D<float4 > A;
            SamplerState SA;
            Texture2D<float4 > B;
            SamplerState SB;
            float4 F(float2 uv) { return A.Sample(SA, uv) + B.Sample(SB, uv); }
            """;

        string fx = Respell(hlsl);

        fx.IndexOf("texture2D B;", StringComparison.Ordinal)
            .ShouldBeLessThan(fx.IndexOf("sampler2D SA", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("float4 Fetch_0(Texture2D<float4 > t_0, SamplerState s_0, float2 uv_0) { return t_0.Sample(s_0, uv_0); }", "'Texture2D t_0'")]
    [InlineData("float4 Fetch_0(Texture2D<float4 > t_0, float2 uv_0) { return t_0.Sample(SpriteSampler, uv_0); }", "'Texture2D t_0'")]
    [InlineData("float4 Fetch_0(float2 uv_0, SamplerState s_0) { return SpriteTexture.Sample(s_0, uv_0); }", "'SamplerState s_0'")]
    public void ResourceFunctionParameter_IsRejectedAsSuch(string helper, string named)
    {
        string unsupported = Rejected(SlangcEmission + "\n" + helper);

        unsupported.ShouldContain("passed as a function parameter", Case.Sensitive);
        unsupported.ShouldContain(named, Case.Sensitive);
    }

    [Fact]
    public void SubscriptLoad_IsRejectedByName()
    {
        string unsupported = Rejected(SlangcEmission.Replace(
            "SpriteTexture.Sample(SpriteSampler, uv_0)", "SpriteTexture[uint2(int2(pos_0.xy))]"));

        unsupported.ShouldContain("subscript load 'SpriteTexture[...]'", Case.Sensitive);
    }

    [Fact]
    public void TextureArrayDeclaration_IsNotMistakenForASubscriptLoad()
    {
        string message = Rejected("Texture2D<float4 > Arr[2];\nSamplerState S;\nfloat4 F(float2 uv) { return Arr[0].Sample(S, uv); }");
        message.ShouldNotContain("subscript load 'Arr", Case.Sensitive);
        message.ShouldContain("the texture array 'Arr[...]'", Case.Sensitive);
    }

    [Fact]
    public void RegisterSpaceZero_IsDropped_OtherSpacesRejected()
    {
        Respell(SlangcEmission.Replace("SamplerState SpriteSampler;", "SamplerState SpriteSampler : register(s1, space0);"))
            .ShouldContain("sampler2D SpriteSampler : register(s1) = sampler_state", Case.Sensitive);

        Rejected(SlangcEmission.Replace("SamplerState SpriteSampler;", "SamplerState SpriteSampler : register(s1, space2);"))
            .ShouldContain("register space on sampler 'SpriteSampler' (register(s1, space2))", Case.Sensitive);
    }

    [Fact]
    public void UnusedSampler_MessageSaysSoOnce()
    {
        string unsupported = Rejected(SlangcEmission + "\nSamplerState Spare;\n");

        unsupported.ShouldContain("the sampler 'Spare'", Case.Sensitive);
        unsupported.Split("which").Length.ShouldBe(2, "one 'which' clause, not two");
    }

    [Fact]
    public void Rejection_CarriesTheSlangSourceLine()
    {
        const string hlsl = """
            #line 1 "core"
            SamplerState Hoisted;
            #line 3 "<stdin>"
            Texture2D<float4 > SpriteTexture;
            SamplerState SpriteSampler;

            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                return SpriteTexture.SampleLevel(SpriteSampler, uv_0, 0.0f);
            }
            """;

        SlangFx2TextureRespeller.Result result = SlangFx2TextureRespeller.Respell(hlsl);

        result.Text.ShouldBeNull();
        result.Unsupported.ShouldNotBeNull().ShouldContain("SampleLevel", Case.Sensitive);
        result.SourceLine.ShouldBe(8, "#line 3 names the line after it; SampleLevel sits five lines below that");
    }

    // ---- Issue #230 follow-up: only REAL resource types are textures/samplers. A user type
    // whose name merely starts with "Texture" or "SamplerState" used to match 'Texture\w*'.
    // slangc keeps the user's type name and appends '_N' (measured: 'TextureRegion_0').

    [Theory]
    [InlineData("TextureRegion_0 r_0")]
    [InlineData("Texture2DInfo_0 r_0")]
    [InlineData("in TextureRegion_0 r_0")]
    [InlineData("SamplerStateInfo_0 r_0")]
    [InlineData("SamplerComparisonStates_0 r_0")]
    public void UserTypeNamedLikeAResource_AsAFunctionParameter_IsNotAResource(string parameter)
    {
        string helper = "float2 Remap_0(" + parameter + ", float2 uv_0) { return uv_0; }";

        string fx = Respell(SlangcEmission + "\n" + helper);

        fx.ShouldContain(helper, Case.Sensitive);
        fx.ShouldContain("float4 c_0 = tex2D(SpriteSampler, uv_0);", Case.Sensitive);
    }

    [Theory]
    [InlineData("TextureSlot_0 slots_0[int(2)];")]
    [InlineData("Texture2DSlot_0 slots_0[int(2)];")]
    [InlineData("TextureSlot_0 slots_0[int(2)]; float2 s_0 = slots_0[i_0].Scale_0;")]
    public void LocalArrayOfAUserTypeNamedLikeATexture_IsNotATextureArrayOrASubscriptLoad(string local)
    {
        string body = "float4 F(float2 uv, int i_0) { " + local + " return float4(uv, 0, 1); }";

        Respell(SlangcEmission + "\n" + body).ShouldContain(body, Case.Sensitive);
    }

    [Fact]
    public void TextureFreeText_WithTextureNamedUserTypes_ComesBackUnchanged()
    {
        // slangc's emission for a shader with no texture at all (v2026.14.1, trimmed).
        const string hlsl = """
            struct TextureRegion_0
            {
                float4 Rect_0;
            };

            float2 Remap_0(TextureRegion_0 r_0, float2 uv_0)
            {
                return r_0.Rect_0.xy + uv_0 * r_0.Rect_0.zw;
            }

            struct TextureSlot_0
            {
                float2 Scale_0;
            };

            float4 MainPS(float4 pos_0 : SV_Position, float2 uv_1 : TEXCOORD0) : SV_TARGET
            {
                TextureRegion_0 r_1;
                TextureSlot_0  slots_0[int(2)];
                return float4(Remap_0(r_1, uv_1) * slots_0[int(1)].Scale_0, 0.0f, 1.0f);
            }
            """;

        Respell(hlsl).ShouldBe(hlsl);
    }

    [Theory]
    // Every real texture object type still counts, whole-token and with the RW prefix.
    [InlineData("Texture1D<float4 > t_0", "'Texture1D t_0'")]
    [InlineData("Texture2DArray<float4 > t_0", "'Texture2DArray t_0'")]
    [InlineData("Texture2DMS<float4 > t_0", "'Texture2DMS t_0'")]
    [InlineData("Texture2DMSArray<float4 > t_0", "'Texture2DMSArray t_0'")]
    [InlineData("Texture3D<float4 > t_0", "'Texture3D t_0'")]
    [InlineData("TextureCube<float4 > t_0", "'TextureCube t_0'")]
    [InlineData("TextureCubeArray<float4 > t_0", "'TextureCubeArray t_0'")]
    [InlineData("RWTexture2D<float4 > t_0", "'RWTexture2D t_0'")]
    [InlineData("SamplerComparisonState s_0", "'SamplerComparisonState s_0'")]
    public void EveryRealResourceType_AsAFunctionParameter_IsStillRejected(string parameter, string named)
    {
        string unsupported = Rejected(
            SlangcEmission + "\nfloat4 Fetch_0(" + parameter + ", float2 uv_0) { return 0; }");

        unsupported.ShouldContain("passed as a function parameter", Case.Sensitive);
        unsupported.ShouldContain(named, Case.Sensitive);
    }

    [Fact]
    public void NonTwoDimensionalTexture_IsRejectedByName()
    {
        const string hlsl = """
            TextureCube<float4 > Env;
            SamplerState S;
            float4 F(float3 d) { return Env.Sample(S, d); }
            """;

        Rejected(hlsl).ShouldContain("TextureCube", Case.Sensitive);
    }
}
