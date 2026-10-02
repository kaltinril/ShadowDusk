#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// GitHub issue #299: an explicit <c>register(sN)</c> on a LEGACY <c>sampler</c> declaration pins
/// the OpenGL texture unit (issue #189), and the <c>.fx</c> route used to read that clause off
/// the raw source. <c>mgfxc</c> reads it off the preprocessed source. Every expected slot below
/// was measured against the pinned <c>mgfxc</c> 3.8.4.1 <c>/Profile:OpenGL</c> (2026-10-02); the
/// committed goldens <c>SamplerLegacyRegisterIfBranch</c> / <c>SamplerLegacyRegisterMacro</c>
/// carry the two-sampler versions and are compared by <c>GlSamplerSlotCorpusTests</c>.
/// Before the fix ShadowDusk emitted <c>ps_s1</c> for every dead-branch shape, <c>ps_s1</c> where
/// the active branch said <c>s2</c>, and <c>ps_s0</c> for every macro-spelled register.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Issue299PreprocessedLegacySamplerRegisterTests
{
    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(120));

    private const string Header = """
        #if OPENGL
        #define PS_SHADERMODEL ps_3_0
        #else
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #endif

        """;

    private const string PixelShader = """

        float4 PS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv); }
        technique T { pass P { PixelShader = compile PS_SHADERMODEL PS(); } }
        """;

    private static async Task<MgfxBlobReader> CompileGl(string source, IReadOnlyList<UserDefine>? defines = null)
    {
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Issue299.fx",
            Defines = defines ?? [],
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        return MgfxBlobReader.Parse(result.Value.Data);
    }

    private static List<MgfxSamplerRecord> PixelSamplers(MgfxBlobReader mgfx)
    {
        var ps = mgfx.Shaders.Single(s => !s.IsVertex);
        return mgfx.Samplers.Where(s => s.ShaderIndex == ps.Index).ToList();
    }

    private static async Task<MgfxSamplerRecord> SinglePixelSampler(
        string source, IReadOnlyList<UserDefine>? defines = null) =>
        PixelSamplers(await CompileGl(source, defines)).Single();

    /// <summary>
    /// The uniform the emitted GLSL actually samples must be the one the record names, or the
    /// table and the shader it describes have drifted apart.
    /// </summary>
    private static void GlslShouldSample(MgfxBlobReader mgfx, string uniform)
    {
        string glsl = System.Text.Encoding.UTF8.GetString(mgfx.Shaders.Single(s => !s.IsVertex).Bytecode);
        glsl.ShouldContain($"uniform sampler2D {uniform};", Case.Sensitive);
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("sampler", "= sampler_state { Texture = <Tex>; }")]
    [InlineData("sampler2D", "= sampler_state { Texture = <Tex>; }")]
    [InlineData("sampler", "{ Texture = <Tex>; }")]
    [InlineData("sampler", "")]
    [InlineData("sampler2D", "")]
    public async Task OpenGl_RegisterOnlyInTheInactiveBranch_LeavesTheSamplerOnUnitZero(string keyword, string body)
    {
        string source = Header + $$"""
            Texture2D Tex;
            #if OPENGL
            {{keyword}} S {{body}};
            #else
            {{keyword}} S : register(s1) {{body}};
            #endif
            """ + PixelShader;

        MgfxBlobReader mgfx = await CompileGl(source);
        MgfxSamplerRecord record = PixelSamplers(mgfx).Single();

        record.Name.ShouldBe("ps_s0",
            customMessage: "mgfxc emits ps_s0: register(s1) is in the branch OpenGL does not compile");
        record.TextureSlot.ShouldBe((byte)0);
        GlslShouldSample(mgfx, "ps_s0");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("= sampler_state { Texture = <Tex>; }")]
    [InlineData("")]
    public async Task OpenGl_RegisterOnlyInTheActiveBranch_IsStillHonoured(string body)
    {
        string source = Header + $$"""
            Texture2D Tex;
            #if OPENGL
            sampler S : register(s1) {{body}};
            #else
            sampler S {{body}};
            #endif
            """ + PixelShader;

        (await SinglePixelSampler(source)).Name.ShouldBe("ps_s1");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("= sampler_state { Texture = <Tex>; }")]
    [InlineData("")]
    public async Task OpenGl_ADifferentRegisterInEachBranch_TakesTheActiveBranchs(string body)
    {
        string source = Header + $$"""
            Texture2D Tex;
            #if OPENGL
            sampler S : register(s2) {{body}};
            #else
            sampler S : register(s1) {{body}};
            #endif
            """ + PixelShader;

        MgfxBlobReader mgfx = await CompileGl(source);
        MgfxSamplerRecord record = PixelSamplers(mgfx).Single();

        record.Name.ShouldBe("ps_s2", customMessage: "mgfxc emits ps_s2, the OpenGL branch's register");
        record.TextureSlot.ShouldBe((byte)2);
        GlslShouldSample(mgfx, "ps_s2");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("sampler", "= sampler_state { Texture = <Tex>; }")]
    [InlineData("sampler2D", "= sampler_state { Texture = <Tex>; }")]
    [InlineData("sampler", "")]
    [InlineData("sampler2D", "")]
    public async Task OpenGl_RegisterNumberSpelledThroughAMacro_IsHonoured(string keyword, string body)
    {
        string source = Header + $$"""
            #define REG s1
            Texture2D Tex;
            {{keyword}} S : register(REG) {{body}};
            """ + PixelShader;

        MgfxBlobReader mgfx = await CompileGl(source);
        MgfxSamplerRecord record = PixelSamplers(mgfx).Single();

        record.Name.ShouldBe("ps_s1",
            customMessage: "mgfxc emits ps_s1, the same as the directly written register(s1)");
        record.TextureSlot.ShouldBe((byte)1);
        GlslShouldSample(mgfx, "ps_s1");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterGatedOnAUserDefine_FollowsTheDefine()
    {
        string source = Header + """
            Texture2D Tex;
            #if PIN_SAMPLER
            sampler S : register(s1) = sampler_state { Texture = <Tex>; };
            #else
            sampler S = sampler_state { Texture = <Tex>; };
            #endif
            """ + PixelShader;

        (await SinglePixelSampler(source)).Name.ShouldBe("ps_s0");
        (await SinglePixelSampler(source, [new UserDefine("PIN_SAMPLER")])).Name.ShouldBe("ps_s1");
    }

    [Fact]
    [Trait("Platform", "OpenGL")]
    public async Task OpenGl_RegisterNumberFromAUserDefine_IsHonoured()
    {
        string source = Header + "sampler S : register(SAMPLER_REG);" + PixelShader;

        (await SinglePixelSampler(source, [new UserDefine("SAMPLER_REG", "s3")])).Name.ShouldBe("ps_s3");
    }

    /// <summary>
    /// The three corpus fixtures whose explicit-slot map CHANGES with this fix. Their
    /// <c>#if SM6</c> arm declares the sampler as a modern <c>SamplerState X : register(sN)</c>
    /// and their OpenGL arm declares the same name through the legacy syntax, so the raw reading
    /// mapped the SM6 arm's register onto the OpenGL arm's texture. With the dead arm gone the
    /// allocator has to reach the same units on its own: declaration order for the single pair,
    /// and for <c>apos-shapes-sm6.fx</c> the legacy arm's own <c>s0</c> / unannotated / <c>s2</c>.
    /// Their OpenGL <c>.mgfx</c> bytes were measured identical before and after (2026-10-02);
    /// this pins the sampler table so that cannot quietly stop being true.
    /// </summary>
    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("VsTransformColorTexture.fx", "ps_s0=SpriteTexture")]
    [InlineData("VsWaveQuadIntrinsics.fx", "ps_s0=SpriteTexture")]
    [InlineData("third-party/Apos.Shapes/apos-shapes-sm6.fx",
        "ps_s0=TextureSampler_SDTexture ps_s1=FontSampler_SDTexture ps_s2=BlueNoiseSampler_SDTexture")]
    public async Task OpenGl_FixturesWhoseRegistersLiveInTheDeadSm6Arm_KeepTheirUnits(string fixture, string expected)
    {
        string path = TestHelpers.FixturePath(fixture);
        using var cts = Cts();
        var result = await new EffectCompiler().CompileAsync(await File.ReadAllTextAsync(path, cts.Token), new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
            SourceFileName = path,
        }, cts.Token);
        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok");

        MgfxBlobReader mgfx = MgfxBlobReader.Parse(result.Value.Data);
        string actual = string.Join(" ", PixelSamplers(mgfx)
            .OrderBy(s => s.TextureSlot)
            .Select(s => $"{s.Name}={mgfx.Parameters[s.Parameter].Name}"));

        actual.ShouldBe(expected);
        PixelSamplers(mgfx).ShouldAllBe(s => s.Name == $"ps_s{s.TextureSlot}");
    }
}
