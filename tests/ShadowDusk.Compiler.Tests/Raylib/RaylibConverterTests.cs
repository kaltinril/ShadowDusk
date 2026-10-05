#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Slang;
using ShadowDusk.Compiler.Tests.Sksl;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests.Raylib;

/// <summary>
/// The Phase 59 raylib converter: HLSL <c>.fx</c> → a raylib <c>glsl330</c> fragment shader for
/// <c>LoadShaderFromMemory(null, fs)</c>. These tests pin the emission's interface and every
/// loud refusal. They are NOT the evidence that the emission renders right: that is
/// <c>validation/RaylibRoute</c>, which renders each shader in real Raylib-cs and pixel-diffs it
/// against the same <c>.fx</c> built for OpenGL in real MonoGame (a GL context, so it cannot run
/// under <c>dotnet test</c> on every host).
///
/// <para>Like the SkSL converter tests, these need the DXC + SPIRV-Cross natives this suite's
/// lanes always carry.</para>
/// </summary>
public sealed class RaylibConverterTests
{
    private static Result<RaylibShader, ShaderError[]> Convert(string fx, string name = "test.fx") =>
        RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = name });

    private static RaylibShader ConvertOk(string fx, string name = "test.fx")
    {
        var result = Convert(fx, name);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value;
    }

    private static ShaderError ConvertFails(string fx, string code, string name = "test.fx")
    {
        var result = Convert(fx, name);
        result.IsFailure.ShouldBeTrue(result.IsSuccess ? result.Value.FragmentShader : "");
        ShaderError error = result.Error.Single();
        error.Code.ShouldBe(code, error.Message);
        error.File.ShouldBe(name);
        return error;
    }

    [Fact]
    public void CrtFilter_EmitsRaylibsFixedInterface_AndNothingSpirvCrossNamed()
    {
        string fx = File.ReadAllText(SkslConverterTests.FindFixture("raylib", "CrtFilter.fx"));
        RaylibShader shader = ConvertOk(fx, "CrtFilter.fx");
        string fs = shader.FragmentShader;

        fs.Split('\n')[0].ShouldBe("#version 330");
        fs.ShouldContain("in vec2 fragTexCoord;", Case.Sensitive);
        fs.ShouldContain("in vec4 fragColor;", Case.Sensitive);
        fs.ShouldContain("out vec4 finalColor;", Case.Sensitive);
        fs.ShouldContain("uniform sampler2D texture0;", Case.Sensitive);
        fs.ShouldContain("uniform vec2 Resolution;", Case.Sensitive);
        fs.ShouldContain("finalColor = ", Case.Sensitive);

        // Nothing of SPIRV-Cross's own interface may survive: raylib binds by these names only.
        fs.ShouldNotContain("in_var_", Case.Sensitive);
        fs.ShouldNotContain("out_var_", Case.Sensitive);
        fs.ShouldNotContain("_Globals", Case.Sensitive);
        fs.ShouldNotContain("type_", Case.Sensitive);
        fs.ShouldNotContain("#version 140", Case.Sensitive);

        // #extension must precede every declaration, so the interface sits after the directives.
        int lastDirective = fs.LastIndexOf("#endif", StringComparison.Ordinal);
        fs.IndexOf("in vec2 fragTexCoord;", StringComparison.Ordinal).ShouldBeGreaterThan(lastDirective);

        shader.Uniforms.Select(u => (u.Name, u.GlslType, u.ArrayLength)).ShouldBe(
        [
            ("Resolution", "vec2", 0),
            ("Curvature", "float", 0),
            ("ScanlineIntensity", "float", 0),
            ("VignetteStrength", "float", 0),
            ("ChromaOffset", "float", 0),
        ]);
        RaylibSampler sampler = shader.Samplers.Single();
        sampler.UniformName.ShouldBe("texture0");
        sampler.HlslTextureName.ShouldBe("SpriteTexture");
        sampler.HlslSamplerName.ShouldBe("SpriteTextureSampler");
        sampler.BoundByDrawCall.ShouldBeTrue();
        sampler.BakedSamplerState.ShouldBeEmpty();
        shader.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void GumGrayscale_MapsItsColor0Tint_ToRaylibsFragColor()
    {
        // The varying the SkSL target must refuse (SD0611) has a real home here: raylib's
        // built-in vertex shader writes the draw call's tint into fragColor, exactly as
        // SpriteBatch writes it into COLOR0. Dropping it would be Gum's hand-port bug.
        string fx = File.ReadAllText(SkslConverterTests.FindFixture("third-party", "Gum", "MonoGameInCode-Grayscale.fx"));
        string fs = ConvertOk(fx, "Grayscale.fx").FragmentShader;

        fs.ShouldContain("in vec4 fragColor;", Case.Sensitive);
        System.Text.RegularExpressions.Regex.Matches(fs, @"\bfragColor\b").Count.ShouldBeGreaterThan(1,
            "fragColor is declared but never read: the COLOR0 tint was dropped");
    }

    [Fact]
    public void SecondSampler_KeepsItsHlslTextureName_AndIsNotTheDrawTexture()
    {
        const string fx = """
            Texture2D Scene;
            sampler2D SceneSampler = sampler_state { Texture = <Scene>; };
            Texture2D Noise;
            sampler2D NoiseSampler = sampler_state { Texture = <Noise>; };
            float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(SceneSampler, uv) * tex2D(NoiseSampler, uv).r * color;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        shader.Samplers.Select(s => (s.UniformName, s.HlslTextureName, s.BoundByDrawCall)).ShouldBe(
            [("texture0", "Scene", true), ("Noise", "Noise", false)]);
        shader.FragmentShader.ShouldContain("uniform sampler2D Noise;", Case.Sensitive);
        shader.FragmentShader.ShouldContain("texture(Noise, fragTexCoord)", Case.Sensitive);
    }

    [Fact]
    public void DrawTexture_FollowsDeclarationOrder_NotFirstUse()
    {
        // MonoGame's SpriteBatch binds unit 0, which mgfxc allocates in DECLARATION order
        // (issue #189). Sampling the second-declared texture first must not move texture0.
        const string fx = """
            Texture2D First;
            sampler2D FirstSampler = sampler_state { Texture = <First>; };
            Texture2D Second;
            sampler2D SecondSampler = sampler_state { Texture = <Second>; };
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                float4 s = tex2D(SecondSampler, uv);
                return s + tex2D(FirstSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        shader.Samplers.Single(s => s.BoundByDrawCall).HlslTextureName.ShouldBe("First");
        shader.Samplers.Single(s => !s.BoundByDrawCall).UniformName.ShouldBe("Second");
    }

    [Fact]
    public void DrawTexture_IgnoresALegacyRegisterInTheBranchOpenGlDoesNotCompile()
    {
        // Issue #299: the converter compiles with the OpenGL macro set, so the #else branch's
        // registers are not this shader's. Read off the raw source they pinned Noise to unit 0
        // and made it the draw texture; mgfxc (and now the converter) allocates in declaration
        // order, so the draw texture is Scene.
        const string fx = """
            Texture2D Scene;
            Texture2D Noise;
            #if OPENGL
            sampler2D SceneSampler = sampler_state { Texture = <Scene>; };
            sampler2D NoiseSampler = sampler_state { Texture = <Noise>; };
            #else
            sampler2D SceneSampler : register(s1) = sampler_state { Texture = <Scene>; };
            sampler2D NoiseSampler : register(s0) = sampler_state { Texture = <Noise>; };
            #endif
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(SceneSampler, uv) * tex2D(NoiseSampler, uv).r;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        shader.Samplers.Select(s => (s.UniformName, s.HlslTextureName, s.BoundByDrawCall)).ShouldBe(
            [("texture0", "Scene", true), ("Noise", "Noise", false)]);
    }

    [Fact]
    public void DrawTexture_FollowsALegacyRegisterSpelledThroughAMacro()
    {
        // Issue #299: `register(NOISE_REGISTER)` is register(s0) once preprocessed, so Noise is
        // the unit-0 sampler even though Scene is declared first. The raw source spells no
        // register number at all, which used to leave Scene as the draw texture.
        const string fx = """
            #define SCENE_REGISTER s1
            #define NOISE_REGISTER s0
            Texture2D Scene;
            sampler2D SceneSampler : register(SCENE_REGISTER) = sampler_state { Texture = <Scene>; };
            Texture2D Noise;
            sampler2D NoiseSampler : register(NOISE_REGISTER) = sampler_state { Texture = <Noise>; };
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(SceneSampler, uv) * tex2D(NoiseSampler, uv).r;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        shader.Samplers.Single(s => s.BoundByDrawCall).HlslTextureName.ShouldBe("Noise");
        shader.Samplers.Single(s => !s.BoundByDrawCall).HlslTextureName.ShouldBe("Scene");
    }

    [Fact]
    public void UniformArray_FlattensToALooseArrayUniform()
    {
        const string fx = """
            float4 Weights[3];
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return Weights[0] * uv.x + Weights[1] * uv.y + Weights[2];
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        shader.FragmentShader.ShouldContain("uniform vec4 Weights[3];", Case.Sensitive);
        shader.Uniforms.Single().ShouldBe(new RaylibUniform("Weights", "vec4", 3));
    }

    [Fact]
    public void BakedSamplerState_IsSurfacedAsAWarningAndInTheContract()
    {
        const string fx = """
            Texture2D Tex;
            sampler2D TexSampler = sampler_state { Texture = <Tex>; Filter = Point; AddressU = Wrap; };
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(TexSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        RaylibShader shader = ConvertOk(fx);

        RaylibSampler sampler = shader.Samplers.Single();
        sampler.BakedSamplerState["Filter"].ShouldBe("Point");
        sampler.BakedSamplerState["AddressU"].ShouldBe("Wrap");
        ShaderError warning = shader.Warnings.Single();
        warning.Code.ShouldBe("SD0637");
        warning.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        warning.Message.ShouldContain("SetTextureFilter", Case.Sensitive);
    }

    private const string TintSlang = """
        Texture2D SpriteTexture;
        SamplerState SpriteTextureSampler;
        float Amount;

        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteTextureSampler, uv) * color * Amount;
        }
        """;

    [Fact]
    public void ConvertSlang_ConvertsSlangInput_AndIsExactlyTheFrontendThenConvert()
    {
        var result = RaylibConverter.ConvertSlang(TintSlang, new RaylibConvertOptions { SourceName = "tint.slang" });
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        RaylibShader shader = result.Value;

        shader.FragmentShader.ShouldContain("uniform float Amount;", Case.Sensitive);
        shader.FragmentShader.ShouldContain("texture(texture0, fragTexCoord)", Case.Sensitive);
        shader.Samplers.Single().HlslTextureName.ShouldBe("SpriteTexture");

        var fx = SlangFrontend.ConvertToFx(TintSlang, new SlangConvertOptions { SourceName = "tint.slang" });
        ConvertOk(fx.Value.FxText, "tint.slang").FragmentShader.ShouldBe(shader.FragmentShader);
    }

    [Fact]
    public void ConvertSlang_RefusesSlangOnlyConstructs_ByName()
    {
        const string slang = """
            import lighting;
            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return 1; }
            """;

        var result = RaylibConverter.ConvertSlang(slang, new RaylibConvertOptions { SourceName = "bad.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0600");
    }

    [Fact]
    public void ConvertSlang_UnsetSourceName_NamesTheSlangSource_NotAnFxFile()
    {
        const string slang = """
            struct V { float4 Position : SV_Position; };
            [shader("vertex")]
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            [shader("fragment")]
            float4 MainPS(V v) : SV_Target { return 1; }
            """;

        var result = RaylibConverter.ConvertSlang(slang, new RaylibConvertOptions());

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().File.ShouldBe("<memory>.slang");
    }

    [Fact]
    public void ConvertSlang_StillRefusesAVertexEntry_WithTheRaylibCode()
    {
        const string slang = """
            struct V { float4 Position : SV_Position; };
            [shader("vertex")]
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            [shader("fragment")]
            float4 MainPS(V v) : SV_Target { return 1; }
            """;

        var result = RaylibConverter.ConvertSlang(slang, new RaylibConvertOptions { SourceName = "vs.slang" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0631");
    }

    // ---- loud refusals ------------------------------------------------------------------

    [Fact]
    public void SecondTechnique_IsRefused_AtTheSecondTechnique()
    {
        const string fx = """
            float4 A() : COLOR0 { return 1; }
            technique One { pass P { PixelShader = compile ps_3_0 A(); } }
            technique Two { pass P { PixelShader = compile ps_3_0 A(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0630");
        (error.Line, error.Column).ShouldBe((3, 1));
        error.Message.ShouldContain("'Two'", Case.Sensitive);
    }

    [Fact]
    public void SecondPass_IsRefused_AtTheSecondPass()
    {
        const string fx = """
            float4 A() : COLOR0 { return 1; }
            technique T
            {
                pass P0 { PixelShader = compile ps_3_0 A(); }
                pass P1 { PixelShader = compile ps_3_0 A(); }
            }
            """;

        ShaderError error = ConvertFails(fx, "SD0630");
        (error.Line, error.Column).ShouldBe((5, 5));
        error.Message.ShouldContain("'P1'", Case.Sensitive);
    }

    [Fact]
    public void VertexShader_IsRefused_AtItsCompileProfile()
    {
        const string fx = """
            struct V { float4 Position : SV_Position; };
            V MainVS(float4 p : POSITION) { V v; v.Position = p; return v; }
            float4 MainPS() : COLOR0 { return float4(1, 0, 0, 1); }
            technique T { pass P {
                VertexShader = compile vs_3_0 MainVS();
                PixelShader = compile ps_3_0 MainPS();
            } }
            """;

        ShaderError error = ConvertFails(fx, "SD0631");
        error.Line.ShouldBe(5);
        error.Column.ShouldBe(28);
        error.Message.ShouldContain("MainVS", Case.Sensitive);
    }

    [Fact]
    public void RenderState_IsRefused_AtTheFirstStateAssignment()
    {
        const string fx = """
            float4 A() : COLOR0 { return 1; }
            technique T
            {
                pass P
                {
                    AlphaBlendEnable = true;
                    SrcBlend = One;
                    PixelShader = compile ps_3_0 A();
                }
            }
            """;

        ShaderError error = ConvertFails(fx, "SD0632");
        (error.Line, error.Column).ShouldBe((6, 9));
        error.Message.ShouldContain("AlphaBlendEnable", Case.Sensitive);
        error.Message.ShouldContain("SrcBlend", Case.Sensitive);
    }

    [Fact]
    public void InterpolantRaylibCannotSupply_IsRefusedByName()
    {
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0, float2 uv2 : TEXCOORD1) : COLOR0
            {
                return float4(uv, uv2);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0633");
        error.Message.ShouldContain("TEXCOORD1", Case.Sensitive);
    }

    [Fact]
    public void Texcoord0WiderThanRaylibsVec2_IsRefused_RatherThanInventingZw()
    {
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float4 uv : TEXCOORD0) : COLOR0
            {
                return uv;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0633");
        error.Message.ShouldContain("vec4", Case.Sensitive);
        error.Message.ShouldContain("float2", Case.Sensitive);
    }

    [Theory]
    [InlineData("return float4(pos.xy / 128.0, 0, 1);", "gl_FragCoord")]
    [InlineData("return float4(ddy(uv), 0, 1);", "dFdy")]
    public void YOrientationDependentConstructs_AreRefused(string body, string construct)
    {
        string fx = $$"""
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                {{body}}
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0634");
        error.Message.ShouldContain(construct, Case.Sensitive);
    }

    [Fact]
    public void HorizontalDerivative_IsNotOrientationDependent_AndConverts()
    {
        const string fx = """
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return float4(ddx(uv), fwidth(uv.x), 1);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ConvertOk(fx).FragmentShader.ShouldContain("dFdx", Case.Sensitive);
    }

    [Fact]
    public void MultipleRenderTargets_AreRefused()
    {
        const string fx = """
            struct PSOut { float4 A : SV_Target0; float4 B : SV_Target1; };
            PSOut MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0)
            {
                PSOut o; o.A = float4(uv, 0, 1); o.B = float4(1, 1, 0, 1); return o;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0634");
        error.Message.ShouldContain("finalColor", Case.Sensitive);
    }

    [Fact]
    public void UniformNamedLikeARaylibBuiltin_IsRefused()
    {
        const string fx = """
            float4 colDiffuse;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return colDiffuse * uv.x;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0635");
        error.Message.ShouldContain("colDiffuse", Case.Sensitive);
    }

    [Fact]
    public void NonDrawTextureNamedLikeAGlslBuiltin_IsRefused()
    {
        const string fx = """
            Texture2D Base;
            sampler2D BaseSampler = sampler_state { Texture = <Base>; };
            Texture2D mix;
            sampler2D MixSampler = sampler_state { Texture = <mix>; };
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return tex2D(BaseSampler, uv) + tex2D(MixSampler, uv);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0635");
        error.Message.ShouldContain("'mix'", Case.Sensitive);
    }

    [Fact]
    public void CubeTexture_IsRefused()
    {
        const string fx = """
            TextureCube Sky;
            SamplerState SkySampler;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return Sky.Sample(SkySampler, float3(uv, 1));
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0635");
        error.Message.ShouldContain("samplerCube", Case.Sensitive);
    }

    [Fact]
    public void ExtraTextureThroughTwoSamplers_IsRefused_BecauseBothUniformsWouldShareItsName()
    {
        const string fx = """
            Texture2D Base;
            Texture2D Tex;
            SamplerState Linear;
            SamplerState Nearest;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return Base.Sample(Linear, uv) + Tex.Sample(Linear, uv) + Tex.Sample(Nearest, uv * 0.5);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0635");
        error.Message.ShouldContain("more than one sampler", Case.Sensitive);
    }

    [Fact]
    public void MatrixUniform_IsRefused()
    {
        const string fx = """
            float4x4 ColorMatrix;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return mul(float4(uv, 0, 1), ColorMatrix);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0636");
        error.Message.ShouldContain("ColorMatrix", Case.Sensitive);
    }

    [Fact]
    public void StructUniform_IsRefused_SoAMatrixInsideItCannotSlipPastTheMatrixRefusal()
    {
        const string fx = """
            struct Grade { float4x4 Mix; float4 Lift; };
            Grade Look;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return mul(float4(uv, 0, 1), Look.Mix) + Look.Lift;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0636");
        error.Message.ShouldContain("'Look'", Case.Sensitive);
    }

    [Fact]
    public void UniformSpirvCrossRenamesForGlsl_IsRefused_BecauseItsLookupNameWouldBindNothing()
    {
        const string fx = """
            float input;
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : COLOR0
            {
                return float4(uv * input, 0, 1);
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        ShaderError error = ConvertFails(fx, "SD0635");
        error.Message.ShouldContain("GetShaderLocation(\"input\")", Case.Sensitive);
    }
}
