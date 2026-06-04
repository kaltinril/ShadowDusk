#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

public sealed class MonoGameGlslRewriterTests
{
    private const string ExampleA = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D _39;

in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 _29 = texture(_39, in_var_TEXCOORD0) * in_var_COLOR0;
    vec3 _36 = vec3(((_29.x + _29.y) + _29.z) * 0.3333333432674407958984375);
    out_var_SV_Target = vec4(_36.x, _36.y, _36.z, _29.w);
}
""";

    private const string ExampleB = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

layout(binding = 0, std140) uniform type_Globals
{
    vec4 TintColor;
} _Globals;

uniform sampler2D _38;

in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = (texture(_38, in_var_TEXCOORD0) * in_var_COLOR0) * _Globals.TintColor;
}
""";

    [Fact]
    public void ExampleA_RewritesToLegacyGlsl()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        // mgfxc form: #define alias + write to ps_oC0 (NOT a raw gl_FragColor write).
        result.Glsl.Should().Contain("#define ps_oC0 gl_FragColor");
        result.Glsl.Should().Contain("ps_oC0 = vec4(");

        // vec4 input -> no swizzle.
        result.Glsl.Should().Contain("* vFrontColor;");

        result.Glsl.Should().NotContain("#version");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("GL_ARB_shading_language_420pack");
    }

    [Fact]
    public void ExampleA_HasNoModernQualifiersOrTextureFn()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        // No leftover "in "/"out " input/output qualifier declarations.
        result.Glsl.Should().NotContain("\nin ");
        result.Glsl.Should().NotContain("\nout ");

        // No bare texture( — only texture2D(.
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"(?<![A-Za-z0-9_])texture\s*\(")
            .Should().BeFalse("only texture2D( should remain");
    }

    [Fact]
    public void ExampleA_SamplersAndNoUniforms()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.UniformRegisterCount.Should().Be(0);
        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Slot.Should().Be(0);
        result.Samplers[0].Name.Should().Be("ps_s0");
    }

    [Fact]
    public void ExampleB_RewritesUniformBlock()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleB, ShaderStage.Pixel);

        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[1];");
        result.Glsl.Should().Contain("ps_uniforms_vec4[0]");
        result.UniformRegisterCount.Should().Be(1);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        result.Glsl.Should().Contain("gl_FragColor");

        result.Glsl.Should().NotContain("#version");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("_Globals");
        result.Glsl.Should().NotContain("type_Globals");
    }

    [Fact]
    public void Vec4Input_NoSwizzle_Vec2Input_GetsXy()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        // vec2 TEXCOORD0 use should be truncated with .xy.
        result.Glsl.Should().Contain("vTexCoord0.xy");

        // vec4 COLOR0 use should NOT get a swizzle appended.
        result.Glsl.Should().NotContain("vFrontColor.xyzw");
        result.Glsl.Should().Contain("* vFrontColor;");
    }

    [Fact]
    public void PrecisionHeaderIsPrepended()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        result.Glsl.Should().StartWith("#ifdef GL_ES");
        result.Glsl.Should().Contain("precision mediump float;");
        result.Glsl.Should().Contain("precision mediump int;");
    }

    // ---- roundEven/round → floor(x+0.5) lowering (WebGL1 reach fix). ----
    // SPIRV-Cross emits roundEven() for HLSL `round` (DXC maps it to OpRoundEven),
    // a GLSL ES 3.00 / GL 1.30 builtin WebGL1 (GLSL ES 1.00) lacks. The rewriter
    // lowers it to floor(x+0.5) — valid everywhere and what mgfxc emits. The PS
    // below is the verbatim SPIRV-Cross GLSL for Pixelated.fx.
    private const string PixelatedRoundEven = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 in_var_TEXCOORD0;
in vec4 in_var_COLOR0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, vec2(roundEven(in_var_TEXCOORD0.x * 32.0) * 0.03125, roundEven(in_var_TEXCOORD0.y * 32.0) * 0.03125));
}
""";

    [Fact]
    public void RoundEven_IsLoweredToFloorHalfUp_AndNoRoundEvenRemains()
    {
        var result = MonoGameGlslRewriter.Rewrite(PixelatedRoundEven, ShaderStage.Pixel);

        // roundEven() is GLSL ES 3.00+ only and MUST NOT survive — it makes the
        // shader fail to load in WebGL1 (KNI Reach profile).
        result.Glsl.Should().NotContain("roundEven", "roundEven is unavailable in GLSL ES 1.00 (WebGL1)");

        // Each roundEven(expr) becomes floor((expr) + 0.5).
        result.Glsl.Should().Contain("floor((vTexCoord0.x * 32.0) + 0.5)");
        result.Glsl.Should().Contain("floor((vTexCoord0.y * 32.0) + 0.5)");
    }

    [Fact]
    public void BareRound_IsLoweredToFloorHalfUp()
    {
        // Defensive: SPIRV-Cross can also emit bare round() (OpRound), also ES-3.00-only.
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_10, vec2(round(in_var_TEXCOORD0.x), in_var_TEXCOORD0.y));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        // No round(/roundEven( call survives; only floor( remains.
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bround(Even)?\s*\(")
            .Should().BeFalse("round/roundEven are unavailable in GLSL ES 1.00 (WebGL1)");
        result.Glsl.Should().Contain("floor((vTexCoord0.x) + 0.5)");
    }

    [Fact]
    public void Round_NestedArgument_BalancedParensLoweredCorrectly()
    {
        // A nested call inside the argument must be captured by the balanced-paren scan.
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = vec4(roundEven(abs(in_var_TEXCOORD0.x) * 8.0));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("roundEven");
        result.Glsl.Should().Contain("floor((abs(vTexCoord0.x) * 8.0) + 0.5)");
    }

    [Fact]
    public void VertexStage_ReturnsInputUnchanged()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Vertex);

        result.Glsl.Should().Be(ExampleA);
        result.Samplers.Should().BeEmpty();
        result.UniformRegisterCount.Should().Be(0);
    }

    // ---- Slang-frontend (browser path) GLSL. SPIRV-Cross names interface vars after
    // Slang's field/entrypoint identifiers (input_Color, entryPointParam_MainPS,
    // GlobalParams_default/globalParams) instead of HLSL semantics; the rewriter's
    // Slang-normalization pre-pass must fold these into the same MojoShader dialect as
    // the DXC examples above. Captured verbatim from the Slang fidelity spike. ----

    private const string SlangGrayscale = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 input_TextureCoordinates;
in vec4 input_Color;
out vec4 entryPointParam_MainPS;

void main()
{
    vec4 _29 = texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, input_TextureCoordinates) * input_Color;
    float _36 = ((_29.x + _29.y) + _29.z) * 0.3333333432674407958984375;
    vec4 _59 = _29;
    _59.x = _36;
    _59.y = _36;
    _59.z = _36;
    entryPointParam_MainPS = _59;
}
""";

    private const string SlangTint = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

layout(binding = 0, std140) uniform GlobalParams_default
{
    vec4 TintColor;
} globalParams;

uniform sampler2D SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler;

in vec2 input_TextureCoordinates;
in vec4 input_Color;
out vec4 entryPointParam_MainPS;

void main()
{
    entryPointParam_MainPS = (texture(SPIRV_Cross_CombinedSpriteTextureSpriteTextureSampler, input_TextureCoordinates) * input_Color) * globalParams.TintColor;
}
""";

    [Fact]
    public void SlangGrayscale_NormalizesToLegacyGlsl()
    {
        var result = MonoGameGlslRewriter.Rewrite(SlangGrayscale, ShaderStage.Pixel);

        // input_TextureCoordinates -> in_var_TEXCOORD0 -> vTexCoord0; input_Color -> vFrontColor.
        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("texture2D(ps_s0, vTexCoord0.xy)");
        // entryPointParam_MainPS -> out_var_SV_Target -> gl_FragColor.
        result.Glsl.Should().Contain("gl_FragColor");

        // No Slang-isms or modern qualifiers survive.
        result.Glsl.Should().NotContain("input_");
        result.Glsl.Should().NotContain("entryPointParam_");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("#version");

        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Name.Should().Be("ps_s0");
        result.UniformRegisterCount.Should().Be(0);
    }

    [Fact]
    public void SlangTint_NormalizesUniformBlockAndInterface()
    {
        var result = MonoGameGlslRewriter.Rewrite(SlangTint, ShaderStage.Pixel);

        // GlobalParams_default { vec4 TintColor; } globalParams -> ps_uniforms_vec4[].
        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[1];");
        result.Glsl.Should().Contain("ps_uniforms_vec4[0]");
        result.UniformRegisterCount.Should().Be(1);

        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("gl_FragColor");

        // No Slang-isms survive.
        result.Glsl.Should().NotContain("input_");
        result.Glsl.Should().NotContain("entryPointParam_");
        result.Glsl.Should().NotContain("globalParams");
        result.Glsl.Should().NotContain("GlobalParams_default");
        result.Glsl.Should().NotContain("type_Globals");
        result.Glsl.Should().NotContain("_Globals");
    }

    // ---- Phase 33: fragment output as mgfxc's `#define ps_oC{N}` alias ----
    // mgfxc emits the PS colour output as `#define ps_oC0 gl_FragColor` and writes to
    // ps_oC0 (verified in tests/fixtures/golden/OpenGL/*.mgfx). KNI's HiDef/WebGL2
    // runtime converter rewrites ONLY that aliased form to `out vec4` under GLSL ES
    // 3.00; a raw `gl_FragColor` write survives and fails (issue #7). These tests pin
    // the alias form, its placement, the SV_Target≡SV_Target0 primary collapse, true
    // MRT, the discard-only case, and the name-collision guard.

    [Fact]
    public void FragmentOutput_EmitsDefineAlias_AndNoRawGlFragColorWrite()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        // The #define alias mgfxc emits (and KNI's ES-3.00 converter needs).
        result.Glsl.Should().Contain("#define ps_oC0 gl_FragColor");

        // The body writes to the alias, not the builtin.
        result.Glsl.Should().Contain("ps_oC0 = vec4(");

        // CRITICAL: no RAW `gl_FragColor =` write may remain — that is exactly what
        // breaks under KNI HiDef/WebGL2 (issue #7). The literal `gl_FragColor` may
        // appear ONLY inside the #define line.
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"gl_FragColor\s*[.\[]?\s*[a-z]*\s*=")
            .Should().BeFalse("a raw gl_FragColor write must not survive — only the #define alias");

        // gl_FragColor appears exactly once, on the #define line.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result.Glsl, "gl_FragColor").Count;
        occurrences.Should().Be(1, "gl_FragColor should appear only in the #define alias");
    }

    [Fact]
    public void FragmentOutput_DefineIsAtColumnZero_BeforeFirstUse()
    {
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);

        int defineIdx = result.Glsl.IndexOf("#define ps_oC0", StringComparison.Ordinal);
        defineIdx.Should().BeGreaterThanOrEqualTo(0);

        // KNI's converter regex is `^#define …` (Multiline) → the alias MUST be at
        // column 0 (line start). And the post-conversion `out vec4 ps_oC0;` must be at
        // global scope before main(), so the #define precedes both main() and the
        // first ps_oC0 use.
        bool atColumnZero = defineIdx == 0 || result.Glsl[defineIdx - 1] == '\n';
        atColumnZero.Should().BeTrue("KNI's converter only matches `#define` at column 0");

        int firstUseIdx = result.Glsl.IndexOf("ps_oC0 =", StringComparison.Ordinal);
        firstUseIdx.Should().BeGreaterThan(defineIdx, "the #define must precede the first ps_oC0 use");

        int mainIdx = result.Glsl.IndexOf("void main", StringComparison.Ordinal);
        defineIdx.Should().BeLessThan(mainIdx, "the #define must be in the header, before main()");
    }

    // Synthetic true-MRT case: three distinct SV_Target outputs.
    private const string MrtThreeOutputs = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;
out vec4 out_var_SV_Target1;
out vec4 out_var_SV_Target2;

void main()
{
    vec4 c = texture(_10, in_var_TEXCOORD0);
    out_var_SV_Target0 = c;
    out_var_SV_Target1 = c.yxzw;
    out_var_SV_Target2 = c.zzzw;
}
""";

    [Fact]
    public void FragmentOutput_TrueMrt_MapsZeroToFragColor_AndRestToFragData()
    {
        var result = MonoGameGlslRewriter.Rewrite(MrtThreeOutputs, ShaderStage.Pixel);

        // Primary (slot 0) → gl_FragColor; slot 1/2 → gl_FragData[N].
        result.Glsl.Should().Contain("#define ps_oC0 gl_FragColor");
        result.Glsl.Should().Contain("#define ps_oC1 gl_FragData[1]");
        result.Glsl.Should().Contain("#define ps_oC2 gl_FragData[2]");

        // All three writes go to the aliases.
        result.Glsl.Should().Contain("ps_oC0 = c;");
        result.Glsl.Should().Contain("ps_oC1 = c.yxzw;");
        result.Glsl.Should().Contain("ps_oC2 = c.zzzw;");

        // No raw builtins survive as writes.
        result.Glsl.Should().NotContain("out_var_");
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"gl_FragData\[\d+\]\s*=")
            .Should().BeFalse("MRT writes target ps_oC{N}, not raw gl_FragData[N]");
    }

    // Single output spelled `SV_Target0` (with the 0) — DXC's name for HLSL `: COLOR0`.
    // SV_Target ≡ SV_Target0 (both PRIMARY); this MUST collapse to ps_oC0/gl_FragColor,
    // NOT gl_FragData[0]. This is the Sepia/Dissolve correctness case.
    private const string SingleOutputTarget0 = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = texture(_10, in_var_TEXCOORD0);
}
""";

    [Fact]
    public void FragmentOutput_SvTarget0_IsPrimary_CollapsesToFragColor_NotFragData()
    {
        var result = MonoGameGlslRewriter.Rewrite(SingleOutputTarget0, ShaderStage.Pixel);

        // SV_Target0 is the PRIMARY single output → gl_FragColor (like mgfxc's golden
        // for Sepia/Dissolve), NOT gl_FragData[0].
        result.Glsl.Should().Contain("#define ps_oC0 gl_FragColor");
        result.Glsl.Should().Contain("ps_oC0 = texture2D(");
        result.Glsl.Should().NotContain("gl_FragData", "a single SV_Target0 output is primary, not MRT");
        result.Glsl.Should().NotContain("#define ps_oC1");
    }

    // Discard-only PS: no colour output at all.
    private const string DiscardOnly = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;

void main()
{
    vec4 c = texture(_10, in_var_TEXCOORD0);
    if (c.w < 0.5)
    {
        discard;
    }
}
""";

    // HLSL semantics are case-insensitive: `: SV_TARGET` and `: sv_target` are the
    // same primary output as `: SV_Target`. DXC mirrors the source spelling, so the
    // rewriter must recognize the output regardless of case (a `: SV_TARGET` return —
    // a very common spelling — must still get the alias, not leak `out_var_SV_TARGET`).
    [Theory]
    [InlineData("out_var_SV_TARGET")]
    [InlineData("out_var_sv_target")]
    [InlineData("out_var_SV_Target0")]
    public void FragmentOutput_CaseInsensitiveSemantic_StillAliasedToPsOc0(string outName)
    {
        string src = $$"""
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 {{outName}};

void main()
{
    {{outName}} = texture(_10, in_var_TEXCOORD0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("#define ps_oC0 gl_FragColor");
        result.Glsl.Should().Contain("ps_oC0 = texture2D(");
        // The raw out_var_* declaration AND use must both be gone (no leak).
        result.Glsl.Should().NotContain("out_var_", "the output decl + uses must be rewritten regardless of case");
        result.Glsl.Should().NotContain("gl_FragData");
    }

    [Fact]
    public void FragmentOutput_DiscardOnly_EmitsNoAliasAndNoFragColor()
    {
        var result = MonoGameGlslRewriter.Rewrite(DiscardOnly, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("#define ps_oC", "a no-output shader has no fragment-output alias");
        result.Glsl.Should().NotContain("gl_FragColor");
        result.Glsl.Should().NotContain("gl_FragData");
        result.Glsl.Should().Contain("discard");
    }

    // Name-collision: the (pathological) source already contains a ps_oC0 identifier.
    private const string CollidingPsOc0 = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 ps_oC0 = texture(_10, in_var_TEXCOORD0);
    out_var_SV_Target = ps_oC0;
}
""";

    [Fact]
    public void FragmentOutput_NameCollision_FailsLoudly()
    {
        // Must NOT silently shadow — fail loudly with a clear message.
        Action act = () => MonoGameGlslRewriter.Rewrite(CollidingPsOc0, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*collision*ps_oC0*");
    }

    // ---- Phase 34: per-dimension texture support (cube / 3D) + LOD/grad ----
    // SPIRV-Cross emits the dimension-specific sampler DECL (samplerCube / sampler3D)
    // but the GENERIC texture() CALL for every dimension. The rewriter must (a) rename
    // the non-2D sampler decl to ps_s{k} keeping its kind, (b) emit the matching
    // dimension-specific builtin (textureCube / texture3D), and (c) carry the right
    // MonoGameSamplerDimension so the pipeline can encode the .mgfx sampler-type byte.

    [Fact]
    public void CubeSampler_RenamedToPsS0_AndCallEmitsTextureCube()
    {
        // Verbatim SPIRV-Cross shape for a TextureCube.Sample (Phase 34 probe).
        const string src = """
#version 140

uniform samplerCube _25;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_25, in_var_TEXCOORD0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("uniform samplerCube ps_s0;",
            "the cube sampler decl must keep its kind and be renamed to ps_s{k}");
        result.Glsl.Should().Contain("textureCube(ps_s0,",
            "a cube sampler must be sampled with textureCube(), not texture2D()");
        result.Glsl.Should().NotContain("texture2D(",
            "the generic texture() must NOT be down-rewritten to texture2D() for a cube sampler");

        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Name.Should().Be("ps_s0");
        result.Samplers[0].Dimension.Should().Be(MonoGameSamplerDimension.TextureCube);
    }

    [Fact]
    public void VolumeSampler_RenamedToPsS0_AndCallEmitsTexture3D()
    {
        const string src = """
#version 140

uniform sampler3D _25;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_25, in_var_TEXCOORD0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("uniform sampler3D ps_s0;");
        result.Glsl.Should().Contain("texture3D(ps_s0,");
        result.Glsl.Should().NotContain("texture2D(");

        result.Samplers.Should().ContainSingle();
        result.Samplers[0].Dimension.Should().Be(MonoGameSamplerDimension.TextureVolume);
    }

    [Fact]
    public void MixedSamplers_EachGetsItsOwnDimensionBuiltin()
    {
        // A 2D + a cube sampler in one shader (the mgfxc EnvironmentMapEffect shape):
        // ps_s0 (2D) -> texture2D, ps_s1 (cube) -> textureCube. Proves the rewrite is
        // PER-sampler, not a blanket dimension.
        const string src = """
#version 140

uniform sampler2D _10;
uniform samplerCube _20;
in vec2 in_var_TEXCOORD0;
in vec3 in_var_TEXCOORD1;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_10, in_var_TEXCOORD0) + texture(_20, in_var_TEXCOORD1);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("uniform sampler2D ps_s0;");
        result.Glsl.Should().Contain("uniform samplerCube ps_s1;");
        result.Glsl.Should().Contain("texture2D(ps_s0,");
        result.Glsl.Should().Contain("textureCube(ps_s1,");

        result.Samplers.Should().HaveCount(2);
        result.Samplers[0].Dimension.Should().Be(MonoGameSamplerDimension.Texture2D);
        result.Samplers[1].Dimension.Should().Be(MonoGameSamplerDimension.TextureCube);
    }

    [Theory]
    [InlineData("textureLod",  "2.0")]   // from tex2Dlod / SampleLevel
    [InlineData("textureGrad", "vec2(0.01, 0.0), vec2(0.0, 0.01)")] // from tex2Dgrad / SampleGrad
    public void LodGradSampling_KeptInGenericForm_NotDownRewritten(string builtin, string extraArgs)
    {
        // Phase 34: the generic LOD/grad form is valid on Desktop + KNI HiDef (core ES
        // 3.00) and is what the rewriter now KEEPS — it must NOT down-rewrite to the
        // legacy texture2DLod/texture2DGrad (which KNI HiDef does not convert), and must
        // NOT fail loudly any more.
        string src = $$"""
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = {{builtin}}(_10, in_var_TEXCOORD0, {{extraArgs}});
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain($"{builtin}(ps_s0,",
            "the generic LOD/grad builtin is the single-blob-correct form (desktop + HiDef)");
        result.Glsl.Should().NotContain("texture2DLod");
        result.Glsl.Should().NotContain("texture2DGrad");
    }

    // ---- Phase 33 → Phase 34: guards remain ONLY for kinds still unmodeled ----
    // cube/3D are now supported; sampler kinds the rewriter still cannot model
    // (sampler2DArray, sampler2DShadow, samplerCubeArray, …) must still FAIL LOUDLY.

    [Theory]
    [InlineData("sampler2DArray")]
    [InlineData("sampler2DShadow")]
    [InlineData("samplerCubeArray")]
    public void Sampling_StillUnmodeledSampler_FailsLoudly(string samplerType)
    {
        string src = $$"""
#version 140

uniform {{samplerType}} _10;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = texture(_10, in_var_TEXCOORD0);
}
""";
        Action act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*Unsupported sampler type*",
                "unmodeled samplers would be silently rewritten to texture2D() — invalid GLSL");
    }

    [Theory]
    [InlineData("samplerCube")]
    [InlineData("sampler3D")]
    public void Sampling_CubeAnd3DSamplers_AreNoLongerGuarded(string samplerType)
    {
        // Regression for the Phase 34 lift: cube/3D must NOT trip the guard any more.
        string src = $$"""
#version 140

uniform {{samplerType}} _10;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = texture(_10, in_var_TEXCOORD0);
}
""";
        Action act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().NotThrow();
    }

    [Fact]
    public void Sampling_Plain2DSampler_IsNotGuarded()
    {
        // Regression: the guard must NOT trip on the normal sampler2D shape.
        Action act = () => MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);
        act.Should().NotThrow();
    }

    [Fact]
    public void ErrorMessage_HasNoPhase34Placeholder()
    {
        // The "(Tracked for Phase 34.)" placeholder must be gone from shipped errors.
        const string src = """
#version 140

uniform sampler2DArray _10;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = texture(_10, in_var_TEXCOORD0);
}
""";
        Action act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .Which.Message.Should().NotContain("Tracked for Phase 34");
    }
}
