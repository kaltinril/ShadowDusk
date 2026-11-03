#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests;

/// <summary>
/// Pins <see cref="ShaderFormatDetector"/>: every supported format is recognized from the text
/// alone with no name, unknown input is <see cref="ShaderFormat.Unknown"/> and not ambiguous,
/// and the overlap cases resolve the same way every time.
/// </summary>
public sealed class ShaderFormatDetectorTests
{
    private const string Fx = """
        Texture2D SpriteTexture;
        sampler2D S = sampler_state { Texture = <SpriteTexture>; };
        float4 MainPS(float4 c : COLOR0, float2 uv : TEXCOORD0) : COLOR { return tex2D(S, uv) * c; }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    private const string Slang = """
        Texture2D SpriteTexture;
        SamplerState SpriteSampler;
        [shader("fragment")]
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
        {
            return SpriteTexture.Sample(SpriteSampler, uv);
        }
        """;

    private const string ShaderToy = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

    // The raylib .fs shape: plain GLSL with a parameterless main.
    private const string PlainGlsl = """
        #version 330
        in vec2 fragTexCoord;
        uniform sampler2D texture0;
        out vec4 finalColor;
        void main()
        {
            finalColor = texture(texture0, fragTexCoord);
        }
        """;

    [Theory]
    [InlineData(Fx, ShaderFormat.FxEffect)]
    [InlineData(Slang, ShaderFormat.Slang)]
    [InlineData(ShaderToy, ShaderFormat.Glsl)]
    [InlineData(PlainGlsl, ShaderFormat.Glsl)]
    [InlineData("import common;\nfloat4 f() { return 0; }", ShaderFormat.Slang)]
    [InlineData("module effects;\nfloat4 f() { return 0; }", ShaderFormat.Slang)]
    [InlineData("void main(void) { gl_FragColor = vec4(1.0); }", ShaderFormat.Glsl)]
    public void RecognizesEachFormat_FromContentAlone(string source, ShaderFormat expected)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source);

        detection.Format.ShouldBe(expected);
        detection.IsAmbiguous.ShouldBeFalse();
        detection.IsRecognized.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("float x = 1.0;\n")]
    [InlineData("<?xml version=\"1.0\"?><root/>")]
    [InlineData("\u0089PNG\r\n\u001a\n")]
    // An HLSL function named main takes parameters, so it is not the GLSL entry.
    [InlineData("void main(out float4 c : COLOR0) { c = 1; }")]
    public void UnknownInput_IsUnknownAndNotAmbiguous(string source)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(source);

        detection.Format.ShouldBe(ShaderFormat.Unknown);
        detection.IsAmbiguous.ShouldBeFalse();
        detection.IsRecognized.ShouldBeFalse();
    }

    [Fact]
    public void KeywordsInsideCommentsAndStrings_DoNotCount()
    {
        string source = "// technique T\n/* void mainImage(out vec4 c, in vec2 p) */\nstatic const char* s = \"[shader(\";\n";

        ShaderFormatDetector.Detect(source).Format.ShouldBe(ShaderFormat.Unknown);
    }

    [Fact]
    public void SlangWithATechniqueBlock_IsReportedAsFx()
    {
        // Owner decision: a file valid as both reports the lowest common denominator, the .fx effect.
        string source = Slang + "\ntechnique T { pass P { PixelShader = compile ps_4_0 MainPS(); } }\n";

        ShaderFormatDetector.Detect(source).Format.ShouldBe(ShaderFormat.FxEffect);
        ShaderFormatDetector.Detect(source, "shader.slang").Format.ShouldBe(ShaderFormat.FxEffect);
    }

    [Fact]
    public void TechniqueAndGlslEntry_IsAmbiguous_EveryTime()
    {
        string source = Fx + "\n" + ShaderToy;

        for (int i = 0; i < 3; i++)
        {
            ShaderFormatDetection detection = ShaderFormatDetector.Detect(source);
            detection.IsAmbiguous.ShouldBeTrue();
            detection.Format.ShouldBe(ShaderFormat.Unknown);
            detection.Reason.ShouldContain("technique", Case.Sensitive);
        }
    }

    [Theory]
    [InlineData("shader.fx", ShaderFormat.FxEffect)]
    [InlineData("content/shader.FX", ShaderFormat.FxEffect)]
    [InlineData("shader.glsl", ShaderFormat.Glsl)]
    [InlineData("shader.fs", ShaderFormat.Glsl)]
    public void NameHint_BreaksTheAmbiguousTie(string name, ShaderFormat expected)
    {
        ShaderFormatDetection detection = ShaderFormatDetector.Detect(Fx + "\n" + ShaderToy, name);

        detection.Format.ShouldBe(expected);
        detection.IsAmbiguous.ShouldBeFalse();
    }

    [Theory]
    [InlineData("shader.slang")]
    [InlineData("shader.txt")]
    [InlineData("noextension")]
    public void NameHint_ThatNamesNeitherSide_LeavesItAmbiguous(string name) =>
        ShaderFormatDetector.Detect(Fx + "\n" + ShaderToy, name).IsAmbiguous.ShouldBeTrue();

    [Theory]
    [InlineData("shader.glsl")]
    [InlineData("shader.slang")]
    [InlineData("shader.txt")]
    public void NameHint_NeverOverridesAContentSignal(string name) =>
        ShaderFormatDetector.Detect(Fx, name).Format.ShouldBe(ShaderFormat.FxEffect);

    [Theory]
    [InlineData("broken.fx", ShaderFormat.FxEffect)]
    [InlineData("broken.slang", ShaderFormat.Slang)]
    [InlineData("broken.frag", ShaderFormat.Glsl)]
    [InlineData("broken.txt", ShaderFormat.Unknown)]
    public void NameHint_DecidesWhenTheContentHasNoSignal(string name, ShaderFormat expected) =>
        ShaderFormatDetector.Detect("float x = 1.0;\n", name).Format.ShouldBe(expected);

    [Fact]
    public void NullSource_Throws() =>
        Should.Throw<ArgumentNullException>(() => ShaderFormatDetector.Detect(null!));
}
