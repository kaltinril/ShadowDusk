#nullable enable

using FluentAssertions;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

public sealed class MonoGameGlslRewriterTests
{
    /// <summary>
    /// The expected GLSL for a <c>float4x4</c> reconstructed from four registers
    /// <paramref name="a"/>..<paramref name="d"/> of <paramref name="prefix"/>, in the
    /// TRANSPOSED form the rewriter now emits (issue #70): registers are taken as the
    /// matrix's ROWS, open-coded with swizzles so no <c>transpose()</c> builtin is needed.
    /// MonoGame/KNI's <c>SetValue(Matrix)</c> uploads register k = column k, and
    /// SPIRV-Cross swaps the multiply operands, so the transpose is what makes the
    /// transform render upright (equivalent to the mgfxc golden's <c>dot(v, c_j)</c>).
    /// </summary>
    private static string Mat4(string prefix, int a, int b, int c, int d)
        => $"mat4(vec4({prefix}[{a}].x, {prefix}[{b}].x, {prefix}[{c}].x, {prefix}[{d}].x), " +
           $"vec4({prefix}[{a}].y, {prefix}[{b}].y, {prefix}[{c}].y, {prefix}[{d}].y), " +
           $"vec4({prefix}[{a}].z, {prefix}[{b}].z, {prefix}[{c}].z, {prefix}[{d}].z), " +
           $"vec4({prefix}[{a}].w, {prefix}[{b}].w, {prefix}[{c}].w, {prefix}[{d}].w))";

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
    public void Round_WhitespaceBeforeParen_IsLoweredCorrectly()
    {
        // FindCallStart tolerates 'round (x)' (space before the paren) — the argument
        // slice must tolerate it too, or the rewrite emits corrupt GLSL like
        // 'floor(((x) + 0.5' (the latent off-by-whitespace bug).
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_10, vec2(round (in_var_TEXCOORD0.x), in_var_TEXCOORD0.y));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bround(Even)?\s*\(")
            .Should().BeFalse("round/roundEven are unavailable in GLSL ES 1.00 (WebGL1)");
        result.Glsl.Should().Contain("floor((vTexCoord0.x) + 0.5)");

        // Balanced parens throughout — the historical bug dropped the closing paren.
        int open  = result.Glsl.Count(c => c == '(');
        int close = result.Glsl.Count(c => c == ')');
        open.Should().Be(close, "the lowered GLSL must keep parentheses balanced");
    }

    // ---- Vertex stage (Phase 28, posFixup contract since Phase 43 F3). SPIRV-Cross
    // VS output for a custom VS taking a float4x4 transform + the SpriteBatch vertex
    // set (POSITION0 / COLOR0 / TEXCOORD0), captured verbatim from DXC→SPIRV-Cross for
    // VsTransformColorTexture.fx. FixupDepthConvention is ON (the depth line below);
    // FlipVertexY is OFF since Phase 43 — the Y-flip is the runtime posFixup
    // uniform's job, injected by the rewriter (mgfxc/MojoShader's contract). ----

    private const string VertexExample = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
    vec4 Tint;
} _Globals;

in vec4 in_var_POSITION0;
in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_COLOR0;
out vec2 out_var_TEXCOORD0;

void main()
{
    gl_Position = _Globals.WorldViewProjection * in_var_POSITION0;
    out_var_COLOR0 = in_var_COLOR0 * _Globals.Tint;
    out_var_TEXCOORD0 = in_var_TEXCOORD0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";

    [Fact]
    public void VertexStage_EmitsMojoShaderDialect()
    {
        var result = MonoGameGlslRewriter.Rewrite(VertexExample, ShaderStage.Vertex);

        // Vertex constant buffer is named vs_uniforms_vec4 (NOT ps_), and a mat4
        // occupies four registers + Tint one => array length 5.
        result.Glsl.Should().Contain("uniform vec4 vs_uniforms_vec4[5];");

        // Inputs become legacy attributes vs_v{k} (vec4, declaration order).
        result.Glsl.Should().Contain("attribute vec4 vs_v0;");
        result.Glsl.Should().Contain("attribute vec4 vs_v1;");
        result.Glsl.Should().Contain("attribute vec4 vs_v2;");

        // Outputs become legacy varyings matching the names the PS reads.
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
        result.Glsl.Should().Contain("varying vec4 vTexCoord0;");

        // gl_Position is written; the matrix is reconstructed (transposed, issue #70)
        // from 4 registers, then multiplied by the position attribute.
        result.Glsl.Should().Contain($"gl_Position = {Mat4("vs_uniforms_vec4", 0, 1, 2, 3)} * vs_v0;");

        // Tint follows the mat4 at register 4.
        result.Glsl.Should().Contain("vs_uniforms_vec4[4]");

        // vec2 TEXCOORD output write is swizzled to .xy (matches mgfxc's vs_oT0.xy form).
        result.Glsl.Should().Contain("vTexCoord0.xy = vs_v2.xy;");

        // No modern qualifiers / interface names / UBO survive.
        result.Glsl.Should().NotContain("#version");
        result.Glsl.Should().NotContain("in_var_");
        result.Glsl.Should().NotContain("out_var_");
        result.Glsl.Should().NotContain("type_Globals");
        result.Glsl.Should().NotContain("_Globals");
        result.Glsl.Should().NotContain("\nin ");
        result.Glsl.Should().NotContain("\nout ");
    }

    // ---- Issue #70: a float4x4 must be reconstructed TRANSPOSED so the vertex
    // transform renders upright in the real MonoGame/KNI runtime.
    //
    // SPIRV-Cross lowers HLSL `mul(v, M)` to GLSL `M * v` (operands swapped, carrying a
    // row/column-major decoration the dialect flatten then strips). MonoGame/KNI's
    // EffectParameter.SetValue(Matrix) uploads register k = column k of the matrix. The
    // mgfxc OpenGL golden therefore computes `result[j] = dot(v, register[j])` — i.e.
    // `v * mat4(reg0..reg3)`. A naive `mat4(reg0..reg3) * v` (registers as columns)
    // computes M·v, the TRANSPOSE — issue #70's "exploded cube". The rewriter reconstructs
    // the transpose (registers as rows, open-coded so no `transpose()` builtin is needed),
    // which is algebraically `mat4(reg0..reg3)ᵀ * v == v * mat4(reg0..reg3) ==
    // dot(reg[i], v)` — exactly the golden's per-row dot. ----

    [Fact]
    public void Matrix_IsReconstructedTransposed_MatchingMgfxcDotForm_Issue70()
    {
        // The exact issue-#70 shape: HLSL `mul(input.Position, xWorldViewProjection)`,
        // which SPIRV-Cross emits (with -Zpr) as `_Globals.M * in_var_POSITION0`.
        const string src = """
#version 140
layout(binding = 0, std140) uniform type_Globals
{
    mat4 xWorldViewProjection;
} _Globals;

in vec4 in_var_POSITION0;
out vec4 out_var_TEXCOORD0;

void main()
{
    gl_Position = _Globals.xWorldViewProjection * in_var_POSITION0;
    out_var_TEXCOORD0 = in_var_POSITION0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        // The transpose: each gl_Position component i becomes dot(register[i], position),
        // matching the mgfxc golden's `vs_o0.x = dot(vs_v0, vs_c0); …` exactly.
        result.Glsl.Should().Contain(Mat4("vs_uniforms_vec4", 0, 1, 2, 3));

        // The naive column reconstruction (the transposed/garbled form) must NOT appear.
        result.Glsl.Should().NotContain(
            "mat4(vs_uniforms_vec4[0], vs_uniforms_vec4[1], vs_uniforms_vec4[2], vs_uniforms_vec4[3])");

        // transpose() must never be emitted — absent in GLSL ES 1.00 (Reach) / desktop 110.
        result.Glsl.Should().NotContain("transpose(");
    }

    // ---- A VS output carrying the legacy D3D9 POSITION/POSITION0 semantic (the form the stock
    // MonoGame GL template emits via `#define SV_POSITION POSITION`) IS the clip position and
    // must be lowered to gl_Position — NOT a `varying`. ShadowDusk's DXC (SM6) frontend makes
    // `: POSITION` an ordinary user output; without this mapping the transform would land in a
    // dead varying and gl_Position would be left UNWRITTEN (silently-broken geometry). ----

    [Theory]
    [InlineData("out_var_POSITION")]   // `: POSITION`  → DXC drops the index
    [InlineData("out_var_POSITION0")]  // `: POSITION0`
    public void VertexPositionSemantic_MapsToGlPosition_NotAVarying(string positionOut)
    {
        string src = $$"""
#version 140
layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
} _Globals;

in vec4 in_var_POSITION0;
in vec4 in_var_COLOR0;
out vec4 {{positionOut}};
out vec4 out_var_COLOR0;

void main()
{
    {{positionOut}} = _Globals.WorldViewProjection * in_var_POSITION0;
    out_var_COLOR0 = in_var_COLOR0;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        // The position write targets the gl_Position builtin...
        result.Glsl.Should().Contain("gl_Position =");
        // ...and the runtime posFixup is appended (proof gl_Position is actually written).
        result.Glsl.Should().Contain("gl_Position.y = gl_Position.y * posFixup.y;");
        // The position output is NOT emitted as a dead varying, and no interface name survives.
        result.Glsl.Should().NotContain("var_POSITION");
        result.Glsl.Should().NotContain(positionOut);
        // A genuine non-position varying (COLOR0) still lowers to its legacy varying.
        result.Glsl.Should().Contain("varying vec4 vFrontColor;");
    }

    // ---- Phase 43 F3: the dynamic posFixup contract. The OpenGL golden
    // VsTransformColorTexture.mgfx VS is string-decisive — its exact lines are:
    //   uniform vec4 posFixup;
    //   gl_Position.y = gl_Position.y * posFixup.y;
    //   gl_Position.xy += posFixup.zw * gl_Position.ww;
    //   gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;   (depth, last)
    // MonoGame 3.8.2's GraphicsDevice.OpenGL.cs sets the uniform at draw time
    // (+1 backbuffer / -1 render target, half-pixel via .zw) and SKIPS programs
    // without it — a baked static flip is wrong for the backbuffer case. ----

    [Fact]
    public void VertexStage_EmitsPosFixupContract_MatchingMgfxcGoldenForm()
    {
        var result = MonoGameGlslRewriter.Rewrite(VertexExample, ShaderStage.Vertex);

        // Declaration directly after the constant-register array (golden order).
        result.Glsl.Should().Contain("uniform vec4 vs_uniforms_vec4[5];\nuniform vec4 posFixup;");

        // The two fixup lines, byte-for-byte the golden's form.
        result.Glsl.Should().Contain("gl_Position.y = gl_Position.y * posFixup.y;");
        result.Glsl.Should().Contain("gl_Position.xy += posFixup.zw * gl_Position.ww;");

        // No static flip survives anywhere (FlipVertexY off + nothing re-bakes it).
        result.Glsl.Should().NotContain("-gl_Position.y");

        // Golden line ORDER: Y-flip, then half-pixel, then the depth-convention line.
        int yFlip  = result.Glsl.IndexOf("gl_Position.y = gl_Position.y * posFixup.y;", StringComparison.Ordinal);
        int halfPx = result.Glsl.IndexOf("gl_Position.xy += posFixup.zw * gl_Position.ww;", StringComparison.Ordinal);
        int depth  = result.Glsl.IndexOf("gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;", StringComparison.Ordinal);
        yFlip.Should().BePositive();
        halfPx.Should().BeGreaterThan(yFlip);
        depth.Should().BeGreaterThan(halfPx,
            "the depth-convention line must stay LAST, matching the mgfxc golden's statement order");
    }

    [Fact]
    public void VertexStage_NoUniformBlock_StillEmitsPosFixup()
    {
        // A passthrough VS (no cbuffer) still needs the posFixup contract or it
        // renders upside-down on the backbuffer in real MonoGame.
        const string src = """
#version 140
in vec4 in_var_POSITION0;
void main()
{
    gl_Position = in_var_POSITION0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        result.Glsl.Should().Contain("uniform vec4 posFixup;");
        result.Glsl.Should().Contain("gl_Position.y = gl_Position.y * posFixup.y;");
        result.Glsl.Should().Contain("gl_Position.xy += posFixup.zw * gl_Position.ww;");
    }

    [Fact]
    public void VertexStage_PosFixupIdentifierCollision_FailsLoudly()
    {
        const string src = """
#version 140
in vec4 in_var_POSITION0;
void main()
{
    vec4 posFixup = in_var_POSITION0;
    gl_Position = posFixup;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*posFixup*");
    }

    [Fact]
    public void VertexStage_AttributeTableCarriesUsageAndIndex()
    {
        var result = MonoGameGlslRewriter.Rewrite(VertexExample, ShaderStage.Vertex);

        result.Attributes.Should().HaveCount(3);
        // POSITION0 -> Position(0)/index 0; COLOR0 -> Color(1)/0; TEXCOORD0 -> TexCoord(2)/0.
        result.Attributes[0].Should().BeEquivalentTo(new { Slot = 0, Name = "vs_v0", Usage = (byte)0, Index = (byte)0 });
        result.Attributes[1].Should().BeEquivalentTo(new { Slot = 1, Name = "vs_v1", Usage = (byte)1, Index = (byte)0 });
        result.Attributes[2].Should().BeEquivalentTo(new { Slot = 2, Name = "vs_v2", Usage = (byte)2, Index = (byte)0 });
    }

    [Fact]
    public void Matrix_ExpandsToFourConsecutiveRegisters_IndicesMatchCbufferLayout()
    {
        // A cbuffer laid out as [mat4 A][float B][mat4 C] occupies registers
        // 0..3 (A), 4 (B), 5..8 (C) — exactly the .mgfx packing
        // (BuildConstantBufferInfoList: a mat4 = four 16-byte registers, a scalar = one).
        // Assert the GLSL indices land on those register offsets so the rewrite agrees
        // with the writer (a transposed/shifted matrix is a silent runtime fidelity bug).
        const string src = """
#version 140
layout(binding = 0, std140) uniform type_Globals
{
    mat4 A;
    float B;
    mat4 C;
} _Globals;

in vec4 in_var_POSITION0;

void main()
{
    gl_Position = (_Globals.A * in_var_POSITION0) * _Globals.B + _Globals.C[0];
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        // Array length = 4 (A) + 1 (B) + 4 (C) = 9 registers.
        result.Glsl.Should().Contain("uniform vec4 vs_uniforms_vec4[9];");
        result.UniformRegisterCount.Should().Be(9);

        // A -> registers 0..3.
        result.Glsl.Should().Contain(Mat4("vs_uniforms_vec4", 0, 1, 2, 3));
        // B -> register 4 (scalar, .x swizzle).
        result.Glsl.Should().Contain("vs_uniforms_vec4[4].x");
        // C -> registers 5..8 (shifted PAST the mat4 A + scalar B).
        result.Glsl.Should().Contain(Mat4("vs_uniforms_vec4", 5, 6, 7, 8));
    }

    [Fact]
    public void PixelStage_Mat4Uniform_ExpandsToFourRegisters_NoTodoLeft()
    {
        // The PS-side mat4 /*TODO mat*/ is resolved: a pixel shader that reads a matrix
        // free-uniform expands to the same 4-register form, and the placeholder is gone.
        const string src = """
#version 140
layout(binding = 0, std140) uniform type_Globals
{
    mat4 ColorMatrix;
} _Globals;

in vec4 in_var_COLOR0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = _Globals.ColorMatrix * in_var_COLOR0;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("TODO mat");
        result.Glsl.Should().Contain(Mat4("ps_uniforms_vec4", 0, 1, 2, 3));
        result.UniformRegisterCount.Should().Be(4);
    }

    [Fact]
    public void VertexStage_UnknownSemantic_ThrowsLoudly()
    {
        const string src = """
#version 140
in vec4 in_var_BLENDWEIGHT0;
void main()
{
    gl_Position = in_var_BLENDWEIGHT0;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*BLENDWEIGHT0*");
    }

    // ---- Phase 43 F4/F5/F6: the GL cbuffer/array model. ----
    // The legacy Slang-normalization pre-pass was REMOVED (the browser path runs the
    // faithful DXC frontend, so Slang-shaped GLSL can no longer reach the rewriter,
    // and its accidental UBO-rename branch was what made a second cbuffer ship as
    // raw invalid GLSL). Named cbuffer blocks are parsed directly; all same-stage
    // blocks merge into ONE {vs,ps}_uniforms_vec4[] register space (MojoShader's
    // model: D3D9 has a single float-constant register file per stage); array
    // members are packed at their element stride; unmodelled members fail loudly.
    // Block shapes below are verbatim SPIRV-Cross output for DXC-compiled HLSL.

    private const string NamedCbufferPs = """
#version 140

layout(binding = 0, std140) uniform type_Transforms
{
    mat4 WorldViewProj;
    vec4 DiffuseColor;
} Transforms;

out vec4 out_var_SV_TARGET;

void main()
{
    out_var_SV_TARGET = Transforms.DiffuseColor;
}
""";

    private const string TwoCbuffersPs = """
#version 140

layout(binding = 0, std140) uniform type_BufA
{
    vec4 TintA;
} BufA;

layout(binding = 1, std140) uniform type_BufB
{
    vec4 TintB;
    float MixAmount;
} BufB;

out vec4 out_var_SV_TARGET;

void main()
{
    out_var_SV_TARGET = mix(BufA.TintA, BufB.TintB, vec4(BufB.MixAmount));
}
""";

    private const string ArraysPs = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 Colors[4];
    float Weights[4];
    vec3 Dirs[2];
    mat4 Mats[2];
    float Selector;
} _Globals;

out vec4 out_var_SV_TARGET;

void main()
{
    int _40 = int(_Globals.Selector);
    vec4 _51 = (_Globals.Colors[_40] * _Globals.Weights[_40]) + (_Globals.Colors[1] * _Globals.Weights[2]);
    vec3 _56 = _51.xyz + (_Globals.Dirs[1] * 0.25);
    out_var_SV_TARGET = vec4(_56.x, _56.y, _56.z, _51.w) + (_Globals.Mats[1] * vec4(0.0, 0.0, 0.0, 1.0));
}
""";

    [Fact]
    public void NamedCbuffer_RewritesToUniformsArray_NoBlockSurvives()
    {
        var result = MonoGameGlslRewriter.Rewrite(NamedCbufferPs, ShaderStage.Pixel);

        // mat4 (4 registers) + vec4 (1 register) = 5; DiffuseColor reads register 4.
        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[5];");
        result.Glsl.Should().Contain("ps_oC0 = ps_uniforms_vec4[4];");
        result.Glsl.Should().NotContain("Transforms");
        result.Glsl.Should().NotContain("std140");
        result.UniformRegisterCount.Should().Be(5);

        // The layout the pipeline builds the .mgfx cbuffer record from.
        result.Uniforms.Should().HaveCount(2);
        result.Uniforms[0].Should().Be(new MonoGameGlslUniform("WorldViewProj", 0, 4));
        result.Uniforms[1].Should().Be(new MonoGameGlslUniform("DiffuseColor", 4, 1));
    }

    [Fact]
    public void TwoCbuffers_MergeIntoOneRegisterSpace_InDeclarationOrder()
    {
        var result = MonoGameGlslRewriter.Rewrite(TwoCbuffersPs, ShaderStage.Pixel);

        // BufA.TintA -> reg 0; BufB.TintB -> reg 1; BufB.MixAmount -> reg 2.x.
        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[3];");
        result.Glsl.Should().Contain("mix(ps_uniforms_vec4[0], ps_uniforms_vec4[1], vec4(ps_uniforms_vec4[2].x))");
        result.Glsl.Should().NotContain("BufA");
        result.Glsl.Should().NotContain("BufB");
        result.Glsl.Should().NotContain("layout(");

        result.Uniforms.Should().Equal(
            new MonoGameGlslUniform("TintA", 0, 1),
            new MonoGameGlslUniform("TintB", 1, 1),
            new MonoGameGlslUniform("MixAmount", 2, 1));
    }

    [Fact]
    public void ArrayMembers_PackAtElementStride_LiteralAndDynamicIndices()
    {
        var result = MonoGameGlslRewriter.Rewrite(ArraysPs, ShaderStage.Pixel);

        // Colors[4] @ 0..3, Weights[4] @ 4..7, Dirs[2] @ 8..9, Mats[2] @ 10..17,
        // Selector @ 18 — total 19 registers.
        result.Glsl.Should().Contain("uniform vec4 ps_uniforms_vec4[19];");

        // Literal indices fold to constant registers.
        result.Glsl.Should().Contain("ps_uniforms_vec4[1] * ps_uniforms_vec4[6].x");
        result.Glsl.Should().Contain("ps_uniforms_vec4[9].xyz * 0.25");

        // Dynamic indices keep the arithmetic in GLSL (MojoShader's relative form).
        result.Glsl.Should().Contain("ps_uniforms_vec4[0 + (_40)]");
        result.Glsl.Should().Contain("ps_uniforms_vec4[4 + (_40)].x");

        // mat4 array elements reconstruct (transposed, issue #70) at stride 4.
        result.Glsl.Should().Contain(Mat4("ps_uniforms_vec4", 14, 15, 16, 17));

        // Scalar after the arrays lands at the shifted register.
        result.Glsl.Should().Contain("ps_uniforms_vec4[18].x");
        result.Glsl.Should().NotContain("_Globals");

        result.Uniforms.Should().Equal(
            new MonoGameGlslUniform("Colors", 0, 4),
            new MonoGameGlslUniform("Weights", 4, 4),
            new MonoGameGlslUniform("Dirs", 8, 2),
            new MonoGameGlslUniform("Mats", 10, 8),
            new MonoGameGlslUniform("Selector", 18, 1));
        result.UniformRegisterCount.Should().Be(19);
    }

    [Fact]
    public void VertexStage_ArrayMember_UsesVsPrefix_AndLayoutMatchesRecord()
    {
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 Offsets[2];
    mat4 Bones[2];
} _Globals;

in vec4 in_var_POSITION;

void main()
{
    gl_Position = (_Globals.Bones[1] * in_var_POSITION) + _Globals.Offsets[1];
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        result.Glsl.Should().Contain("uniform vec4 vs_uniforms_vec4[10];");
        result.Glsl.Should().Contain(Mat4("vs_uniforms_vec4", 6, 7, 8, 9));
        result.Glsl.Should().Contain("vs_uniforms_vec4[1]");
        // posFixup still injected after the merged declaration.
        result.Glsl.Should().Contain("uniform vec4 posFixup;");

        result.Uniforms.Should().Equal(
            new MonoGameGlslUniform("Offsets", 0, 2),
            new MonoGameGlslUniform("Bones", 2, 8));
    }

    [Theory]
    [InlineData("    int Mode;", "*integer/boolean uniforms*")]
    [InlineData("    bool Flag;", "*integer/boolean uniforms*")]
    [InlineData("    ivec4 Counts;", "*integer/boolean uniforms*")]
    [InlineData("    mat3 Rot;", "*float4x4*")]
    [InlineData("    mat2 Small;", "*float4x4*")]
    public void UnmodeledUniformMember_FailsLoudly(string memberLine, string expectedMessage)
    {
        string src = $$"""
#version 140

layout(binding = 0, std140) uniform type_Globals
{
{{memberLine}}
    vec4 Tint;
} _Globals;

out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = _Globals.Tint;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>().WithMessage(expectedMessage);
    }

    [Fact]
    public void ArrayMember_WholeArrayUse_FailsLoudly()
    {
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 Colors[4];
} _Globals;

out vec4 out_var_SV_Target;

vec4 pick(vec4 c[4]) { return c[0]; }

void main()
{
    out_var_SV_Target = pick(_Globals.Colors);
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*whole-array use*");
    }

    [Fact]
    public void UnrewrittenBlockInstanceReference_FailsLoudly()
    {
        // A use shape the member rewrites don't cover (here: the instance used
        // bare). Before Phase 43C this shipped as invalid GLSL with exit code 0.
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 Tint;
} _Globals;

out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = _Globals . Tint;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*_Globals*");
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
    public void FragmentOutput_TrueMrt_MapsAllSlotsToFragData_IncludingZero()
    {
        var result = MonoGameGlslRewriter.Rewrite(MrtThreeOutputs, ShaderStage.Pixel);

        // TRUE MRT (2+ outputs): EVERY slot, including slot 0, maps to gl_FragData[N] —
        // matching the mgfxc DeferredSprite GL golden (`#define ps_oC0 gl_FragData[0]`).
        // Slot 0 must NOT be gl_FragColor here: in legacy GLSL with multiple render
        // targets bound, gl_FragColor broadcasts to ALL attachments and corrupts the
        // other target(s) (a real render bug, not cosmetic). gl_FragData[0] writes only
        // attachment 0. (The single-output case keeps gl_FragColor — see the test below.)
        result.Glsl.Should().Contain("#define ps_oC0 gl_FragData[0]");
        result.Glsl.Should().Contain("#define ps_oC1 gl_FragData[1]");
        result.Glsl.Should().Contain("#define ps_oC2 gl_FragData[2]");
        result.Glsl.Should().NotContain("gl_FragColor",
            because: "true MRT must not write gl_FragColor (it would broadcast to all attachments)");

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
    [InlineData("textureLod",  "2.0",                               "texture2DLod")]   // from tex2Dlod / SampleLevel
    [InlineData("textureGrad", "vec2(0.01, 0.0), vec2(0.0, 0.01)", "texture2DGrad")]  // from tex2Dgrad / SampleGrad
    public void LodGradSampling_RewrittenToLegacyName_WithGuardedHeader(string builtin, string extraArgs, string legacy)
    {
        // Phase 43 F7: the generic textureLod/textureGrad forms only exist from GLSL
        // 1.30 / ES 3.00 — Mesa's strict front-end rejects them in the versionless
        // legacy dialect ("no function with name 'textureLod'", the confirmed Linux
        // DesktopGL load failure). The faithful MojoShader form is the
        // dimension-specific legacy name + the guarded extension header; the header's
        // __VERSION__ >= 300 branch maps the legacy name back for KNI HiDef, so the
        // one-artifact-two-profiles promise (Phase 33) holds.
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

        result.Glsl.Should().Contain($"{legacy}(ps_s0,",
            "the dimension-specific legacy spelling is the MojoShader-faithful, Mesa-valid form");

        // No generic CALL survives (the header's `#define texture2DLod textureLod`
        // mention is fine — it is not a call site).
        System.Text.RegularExpressions.Regex.IsMatch(result.Glsl, $@"\b{builtin}\s*\(")
            .Should().BeFalse($"no generic {builtin}( call site may survive in the body");

        // The guarded extension header (MojoShader prepend_glsl_texlod_extensions +
        // its GLSLES3 mapping): graceful degrade, never a compile failure.
        result.Glsl.Should().Contain("#if __VERSION__ >= 300");
        result.Glsl.Should().Contain($"#define {legacy} {builtin}");
        result.Glsl.Should().Contain("#elif defined(GL_ARB_shader_texture_lod)");
        result.Glsl.Should().Contain("#extension GL_ARB_shader_texture_lod : enable");
        result.Glsl.Should().Contain("#elif defined(GL_EXT_gpu_shader4)");
        result.Glsl.Should().Contain("#define texture2DLod(a,b,c) texture2D(a,b)");
    }

    [Theory]
    [InlineData("samplerCube", "vec3 in_var_TEXCOORD0",
        "textureLod(_10, in_var_TEXCOORD0, 2.0)", "textureCubeLod(ps_s0,")]
    [InlineData("sampler3D", "vec3 in_var_TEXCOORD0",
        "textureLod(_10, in_var_TEXCOORD0, 1.0)", "texture3DLod(ps_s0,")]
    public void LodSampling_NonTwoDSamplers_GetDimensionSpecificLodName(
        string samplerType, string inDecl, string call, string expected)
    {
        // SPIRV-Cross emits the GENERIC textureLod for every dimension; the legacy
        // name must follow the SAMPLER's dimension (MojoShader emit_GLSL_TEXLDL:
        // texture2DLod / textureCubeLod / texture3DLod).
        string src = $$"""
#version 140

uniform {{samplerType}} _10;
in {{inDecl}};
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = {{call}};
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        result.Glsl.Should().Contain(expected);
    }

    [Fact]
    public void GradSampling_CubeSampler_FailsLoudly()
    {
        // No GLSL profile or extension defines a textureCubeGrad-style legacy name
        // (MojoShader emits one anyway — GLSL that can never link). Fail loudly.
        const string src = """
#version 140

uniform samplerCube _10;
in vec3 in_var_TEXCOORD0;
out vec4 out_var_SV_Target0;

void main()
{
    out_var_SV_Target0 = textureGrad(_10, in_var_TEXCOORD0, vec3(0.01), vec3(0.01));
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*Gradient sampling*cube*");
    }

    [Fact]
    public void PlainSampling_DoesNotEmitTexLodHeader()
    {
        // The guarded header is emitted ONLY when a LOD/grad/proj call was rewritten —
        // the ordinary corpus output must stay byte-identical to the pre-F7 form.
        var result = MonoGameGlslRewriter.Rewrite(ExampleA, ShaderStage.Pixel);
        result.Glsl.Should().NotContain("GL_ARB_shader_texture_lod");
        result.Glsl.Should().NotContain("__VERSION__");
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
    public void VertexStage_SamplerDeclaration_FailsLoudly()
    {
        // Phase 43 F8: MonoGame 3.8.2's GL runtime never assigns texture units to
        // vertex-shader samplers (ShaderProgramCache.Link applies only the pixel
        // shader's sampler records; no GL VertexTextures path exists), so any emitted
        // VS sampler would silently read the wrong texture at runtime. The old
        // behavior shipped the un-renamed decl (`uniform sampler2D _35;`) while the
        // .mgfx sampler record said ps_s0 — silently-black output, twice broken.
        const string src = """
#version 140

uniform sampler2D _35;
in vec4 in_var_POSITION0;
in vec2 in_var_TEXCOORD0;
out vec2 out_var_TEXCOORD0;

void main()
{
    gl_Position = in_var_POSITION0 + textureLod(_35, in_var_TEXCOORD0, 0.0);
    out_var_TEXCOORD0 = in_var_TEXCOORD0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);
        act.Should().Throw<MonoGameGlslRewriteException>()
            .WithMessage("*Vertex-stage texture sampling*");
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

    // ---- do { … } while(false) elimination (issues #107 + #136). ----
    // SPIRV-Cross renders an early `return` (the entry point's own, or a nested `if`
    // that returns inside an inlined helper) as a single-iteration
    // `do { … break; … } while(false);` loop. Rule 9a (#136) UNWRAPS the loop when it
    // is a direct child of main followed only by simple tail statements: plain block +
    // loop-level `break` → tail + `return;`. Any loop 9a cannot prove safe falls back
    // to Rule 9b (#107): the WebGL1-safe `for (int _i = 0; _i < 1; _i++) { … }` form.
    // The unwrap matters because ANGLE's D3D11 backend (WebGL on every Windows
    // browser) silently zeroes ALL gradient ops (dFdx/dFdy) inside any loop with a
    // divergent exit — a conditional break OR discard — so the for-loop form is
    // load-safe but derivative-poisoned there.

    [Fact]
    public void MainOneShotDoWhile_IsUnwrapped_BreaksBecomeTailPlusReturn()
    {
        // The verbatim SPIRV-Cross shape from issue #107 (nested-if early return),
        // which is also the issue-#136 poisoning shape when lowered to a for-loop.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _v;
    do
    {
        if (in_var_TEXCOORD0.x <= 0.5)
        {
            _v = 0.0;
            break;
        }
        _v = in_var_TEXCOORD0.x;
        break;
    } while(false);
    out_var_SV_Target = vec4(_v, _v, _v, 1.0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        // No do-while survives — it fails to load in WebGL1 (KNI Reach).
        result.Glsl.Should().NotContain("while(false)", "do-while is unavailable in GLSL ES 1.00 (WebGL1)");
        System.Text.RegularExpressions.Regex.IsMatch(result.Glsl, @"\bdo\b")
            .Should().BeFalse("the do keyword must be gone");
        // Issue #136: no loop wrapper either — a for-loop with a conditional break
        // poisons every gradient op on ANGLE D3D11. The loop is unwrapped and each
        // loop-level break becomes the duplicated output-write tail + return.
        result.Glsl.Should().NotContain("_spvonce_",
            "the main wrapper must be UNWRAPPED, not lowered to a for-loop (issue #136)");
        result.Glsl.Should().Contain("{ ps_oC0 = vec4(_v, _v, _v, 1.0); return; }",
            "each loop-level break becomes the tail statements plus an early return");
        // The in-place tail still serves the fall-through path.
        result.Glsl.Should().Contain("\n    ps_oC0 = vec4(_v, _v, _v, 1.0);");
    }

    [Fact]
    public void NestedOneShotDoWhile_BothUnwrapped_NoLoopsRemain()
    {
        const string src = """
#version 140

out vec4 out_var_SV_Target;

void main()
{
    float _v = 0.0;
    do
    {
        do
        {
            _v = 1.0;
            break;
        } while(false);
        break;
    } while(false);
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        // The outer (main-level) wrapper unwraps first; the inner one-shot then sits in
        // the plain block 9a left behind, whose tail ends in the outer break's
        // `{ … return; }` — a terminating exit the recursive scan flattens through, so
        // the inner loop unwraps too. NO loop of any kind survives (issue #136).
        result.Glsl.Should().NotContain("_spvonce_");
        result.Glsl.Should().Contain("{ ps_oC0 = vec4(_v); return; }");
    }

    [Fact]
    public void InlinedHelperGradient_InnerOneShotUnwrapped_GradientNotInAnyLoop()
    {
        // The issue-#136 residual shape found in review: a helper that BOTH
        // early-returns AND takes a derivative. SPIRV-Cross nests the helper's one-shot
        // wrapper inside the entry wrapper; v1 of the unwrap left the inner one as a 9b
        // for-loop — with fwidth inside a loop with a conditional break, i.e. exactly
        // the ANGLE-poisoned shape the fix exists to remove.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 _38;
    do
    {
        if (in_var_TEXCOORD0.x > 0.99)
        {
            _38 = vec4(0.0);
            break;
        }
        float _29 = in_var_TEXCOORD0.y * 30.0;
        float _36;
        do
        {
            if (_29 > 100.0)
            {
                _36 = 0.0;
                break;
            }
            _36 = fwidth(_29);
            break;
        } while(false);
        _38 = vec4(_36, 0.0, 0.0, 1.0);
        break;
    } while(false);
    out_var_SV_Target = _38;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().NotContain("_spvonce_",
            "both the entry wrapper AND the inlined helper's wrapper must unwrap — a " +
            "for-loop with a conditional break around fwidth is ANGLE-poisoned (issue #136)");
        result.Glsl.Should().Contain("fwidth(_29)", "the derivative itself is untouched");
        // The inner loop's breaks got the FLATTENED tail: the statements after the
        // inner loop plus the contents of the outer break's return-block.
        result.Glsl.Should().Contain("{ _38 = vec4(_36, 0.0, 0.0, 1.0); ps_oC0 = _38; return; }");
    }

    [Fact]
    public void TailContainingGradientOp_FallsBackToForLoop_KeepsItConvergent()
    {
        // Review finding: duplicating the tail into a break site moves its statements
        // into a divergent branch. A gradient op (or implicit-LOD sample) there is
        // undefined (GLSL §8.13.1) — in the original do-while it executed AFTER the
        // loop, convergently. Such tails must keep the 9b for-loop form.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _29;
    do
    {
        if (in_var_TEXCOORD0.x > 0.75)
        {
            _29 = 0.0;
            break;
        }
        _29 = length(in_var_TEXCOORD0.xy) - 0.5;
        break;
    } while(false);
    out_var_SV_Target = vec4(fwidth(_29), _29, 0.0, 1.0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().MatchRegex(@"for \(int _spvonce_0 = 0; _spvonce_0 < 1; _spvonce_0\+\+\)",
            "a tail containing fwidth must not be duplicated into divergent branches");
        result.Glsl.Should().NotContain("return;");
        // The gradient stays where it was: after the loop, in convergent flow (which
        // ANGLE does NOT poison — only ops inside the divergent loop are affected).
        result.Glsl.Should().MatchRegex(@"\}\s*\n\s*ps_oC0 = vec4\(fwidth\(_29\)");
    }

    [Fact]
    public void TailContainingImplicitLodSample_FallsBackToForLoop()
    {
        // Same rationale as the gradient-tail case: implicit-LOD texture sampling
        // derives its mip level from screen-space derivatives, so it is equally
        // divergence-sensitive. (texture( is rewritten to texture2D( by Rule 6 before
        // Rule 9 runs, so the guard sees the legacy spelling.)
        const string src = """
#version 140

uniform sampler2D SpriteTexture;

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _33;
    do
    {
        if (in_var_TEXCOORD0.x > 0.75)
        {
            _33 = 0.0;
            break;
        }
        _33 = in_var_TEXCOORD0.x * 2.0;
        break;
    } while(false);
    out_var_SV_Target = texture(SpriteTexture, in_var_TEXCOORD0) * _33;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().MatchRegex(@"for \(int _spvonce_0 = 0; _spvonce_0 < 1; _spvonce_0\+\+\)",
            "a tail containing an implicit-LOD sample must not be duplicated into divergent branches");
        result.Glsl.Should().NotContain("return;");
        result.Glsl.Should().Contain("texture2D(ps_s0", "the sample stays after the loop, convergent");
    }

    [Fact]
    public void GenuineInnerLoopBreak_IsPreserved_WhileMainWrapperUnwraps()
    {
        // The apos-shapes shape: the entry wrapper contains a REAL bounded loop (the
        // ellipse-SDF Newton iteration) with its own conditional break. That break's
        // nearest enclosing loop is the inner for — it must stay a break; only the
        // wrapper-level breaks become returns.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _v;
    do
    {
        float acc = 0.0;
        for (int i = 0; i < 8; i++)
        {
            acc += in_var_TEXCOORD0.x;
            if (acc > 4.0)
            {
                break;
            }
        }
        if (acc < 0.5)
        {
            _v = 0.0;
            break;
        }
        _v = acc;
        break;
    } while(false);
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().NotContain("_spvonce_", "the main wrapper must unwrap, not lower");
        // The genuine inner loop and its own break are untouched.
        result.Glsl.Should().Contain("for (int i = 0; i < 8; i++)");
        result.Glsl.Should().MatchRegex(@"if \(acc > 4\.0\)\s*\{\s*break;\s*\}",
            "the inner for-loop's break binds to the inner loop and must be preserved");
        // The wrapper-level breaks became tail + return.
        result.Glsl.Should().Contain("{ ps_oC0 = vec4(_v); return; }");
    }

    [Fact]
    public void MultiStatementTail_IsDuplicatedWholeBeforeReturn()
    {
        const string src = """
#version 140

out vec4 out_var_SV_Target;

void main()
{
    float _v;
    float _w;
    do
    {
        _v = 1.0;
        break;
    } while(false);
    _w = _v * 2.0;
    out_var_SV_Target = vec4(_w);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().NotContain("_spvonce_");
        result.Glsl.Should().Contain("{ _w = _v * 2.0; ps_oC0 = vec4(_w); return; }",
            "ALL tail statements are duplicated, in order, before the return");
    }

    [Fact]
    public void LoopLevelContinue_FallsBackToForLoopLowering()
    {
        // A `continue` at the one-shot loop's level exits identically to break in a
        // do-while(false) — the for-loop fallback preserves that without a rewrite,
        // so the unwrap must NOT fire.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _v = 0.0;
    do
    {
        if (in_var_TEXCOORD0.x > 0.5)
        {
            continue;
        }
        _v = 1.0;
    } while(false);
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().MatchRegex(@"for \(int _spvonce_0 = 0; _spvonce_0 < 1; _spvonce_0\+\+\)");
        result.Glsl.Should().Contain("continue;", "the fallback keeps the continue's exit semantics");
    }

    [Fact]
    public void OneShotNotDirectChildOfMain_FallsBackToForLoopLowering()
    {
        // Inside an if, "everything after the loop" is not statically main's tail —
        // the unwrap cannot prove a return-rewrite, so Rule 9b handles it.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _v = 0.0;
    if (in_var_TEXCOORD0.y > 0.0)
    {
        do
        {
            if (in_var_TEXCOORD0.x <= 0.5)
            {
                break;
            }
            _v = 1.0;
        } while(false);
        _v += 0.25;
    }
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().MatchRegex(@"for \(int _spvonce_0 = 0; _spvonce_0 < 1; _spvonce_0\+\+\)");
        result.Glsl.Should().NotContain("return;",
            "no return may be synthesized for a loop that is not main's own wrapper");
    }

    [Fact]
    public void DiscardInsideWrapper_SurvivesTheUnwrap()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _v;
    do
    {
        if (in_var_TEXCOORD0.x < 0.0)
        {
            discard;
        }
        _v = in_var_TEXCOORD0.x;
        break;
    } while(false);
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().NotContain("_spvonce_");
        result.Glsl.Should().Contain("discard;", "discard is stage-terminating and needs no rewrite");
        result.Glsl.Should().Contain("{ ps_oC0 = vec4(_v); return; }");
    }

    [Fact]
    public void GenuineMultiIterationDoWhile_IsLeftUntouched()
    {
        // A real do-while (condition is NOT the literal `false`) must NOT be rewritten —
        // only SPIRV-Cross's structured-early-return one-shot form is the target.
        const string src = """
#version 140

uniform vec4 ps_uniforms_vec4[1];
out vec4 out_var_SV_Target;

void main()
{
    float _v = 0.0;
    int _i = 0;
    do
    {
        _v += 0.1;
        _i++;
    } while(_i < 4);
    out_var_SV_Target = vec4(_v);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("while(_i < 4)", "a genuine multi-iteration do-while is preserved");
        result.Glsl.Should().NotContain("_spvonce_", "no one-shot lowering should fire on a real loop");
    }

    // ---- pow(x, 2.0) → multiply strength reduction (issue #127). ----
    // GLSL leaves pow undefined for a negative base (drivers lowering to
    // exp2(y*log2(x)) return NaN), while fxc constant-folds pow(x, 2) into a
    // multiply — so HLSL squaring a possibly-negative value via pow() (Apos.Shapes'
    // LinearGradient squares normalized-direction components) is well-defined
    // through mgfxc but driver-dependent through native GLSL pow. The source below
    // is the shape SPIRV-Cross emits for apos-shapes.fx (issue #127).

    [Fact]
    public void PowSquare_SimpleOperands_AreStrengthReducedToMultiply_Issue127()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _1286 = in_var_TEXCOORD0.x - 0.5;
    float _1293 = in_var_TEXCOORD0.y - 0.5;
    float _d = sqrt(pow(_1286, 2.0) + pow(_1293, 2.0));
    float _g = pow(abs(_1286), 2.400000095367431640625);
    out_var_SV_Target = vec4(_d, _g, 0.0, 1.0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotMatchRegex(@"pow\([^,()]*, 2\.0\)",
            because: "pow with a possibly-negative base is undefined in GLSL — it must become a multiply");
        result.Glsl.Should().Contain("((_1286) * (_1286))");
        result.Glsl.Should().Contain("((_1293) * (_1293))");

        // Non-2.0 exponents keep their (abs-guarded) pow — only squaring is reduced.
        result.Glsl.Should().Contain("pow(abs(_1286), 2.400000095367431640625)");
    }

    [Fact]
    public void PowSquare_SwizzleAndSignedOperands_AreReduced()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _a = pow(in_var_TEXCOORD0.x, 2.0) + pow(-in_var_TEXCOORD0.y, 2.0);
    out_var_SV_Target = vec4(_a);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("2.0)", "both squares must be reduced to multiplies");
        result.Glsl.Should().Contain("((vTexCoord0.x) * (vTexCoord0.x))");
        result.Glsl.Should().Contain("((-vTexCoord0.y) * (-vTexCoord0.y))");
    }

    [Fact]
    public void PowSquare_ComplexBase_IsLeftUntouched()
    {
        // A non-trivial base (a call or compound expression) must NOT be duplicated
        // textually — the conservative gate leaves the original pow in place.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _a = pow(in_var_TEXCOORD0.x + 0.25, 2.0);
    float _b = pow(fract(in_var_TEXCOORD0.y), 2.0);
    out_var_SV_Target = vec4(_a, _b, 0.0, 1.0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("pow(vTexCoord0.x + 0.25, 2.0)",
            because: "a compound base is not a simple operand — duplication could change cost/semantics");
        result.Glsl.Should().Contain("pow(fract(vTexCoord0.y), 2.0)",
            because: "a call base is never duplicated");
    }

    // ---- 1.0 / (a / b) → b / a reciprocal-of-quotient fold (issue #127). ----
    // SPIRV-Cross preserves the HLSL `1.0 / (aaSize / length(...))` shape literally
    // (fxc folds it), costing an extra rounding at every SmoothDiscontinuity site in
    // apos-shapes.fx. One correctly-rounded division replaces two; zero/infinity edge
    // cases are value-identical.

    [Fact]
    public void ReciprocalOfQuotient_IsFoldedToSingleDivision_Issue127()
    {
        const string src = """
#version 140

in vec4 in_var_TEXCOORD5;
in vec4 in_var_TEXCOORD3;
out vec4 out_var_SV_Target;

void main()
{
    float _1200 = 1.0 / (in_var_TEXCOORD5.y / (6.283185482025146484375 * length(in_var_TEXCOORD3.xy)));
    float _1077 = 1.0 / (in_var_TEXCOORD5.y / length(in_var_TEXCOORD3.zw));
    out_var_SV_Target = vec4(_1200, _1077, 0.0, 1.0);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("1.0 / (",
            because: "the reciprocal-of-quotient must fold to a single division");
        result.Glsl.Should().Contain("((6.283185482025146484375 * length(vTexCoord3.xy)) / (vTexCoord5.y))");
        result.Glsl.Should().Contain("((length(vTexCoord3.zw)) / (vTexCoord5.y))");
    }

    [Fact]
    public void ReciprocalOfQuotient_AmbiguousShapes_AreLeftUntouched()
    {
        // Shapes where the parenthesized group's root operator is not provably the
        // division, or where 1.0 does not begin its term, must NOT be folded.
        const string src = """
#version 140

in vec4 in_var_TEXCOORD5;
out vec4 out_var_SV_Target;

void main()
{
    float _a = 1.0 / (in_var_TEXCOORD5.x / in_var_TEXCOORD5.y * in_var_TEXCOORD5.z);
    float _b = 1.0 / (in_var_TEXCOORD5.x + in_var_TEXCOORD5.y / in_var_TEXCOORD5.z);
    float _c = in_var_TEXCOORD5.w * 1.0 / (in_var_TEXCOORD5.x / in_var_TEXCOORD5.y);
    float _d = 21.0 / (in_var_TEXCOORD5.x / in_var_TEXCOORD5.y);
    out_var_SV_Target = vec4(_a, _b, _c, _d);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("1.0 / (vTexCoord5.x / vTexCoord5.y * vTexCoord5.z)",
            because: "the trailing * makes the multiply, not the division, the group's root");
        result.Glsl.Should().Contain("1.0 / (vTexCoord5.x + vTexCoord5.y / vTexCoord5.z)",
            because: "a top-level additive operator means the division is not the root");
        result.Glsl.Should().Contain("* 1.0 / (vTexCoord5.x / vTexCoord5.y)",
            because: "here 1.0 is the right operand of a multiply, not the start of its term");
        result.Glsl.Should().Contain("21.0 / (vTexCoord5.x / vTexCoord5.y)",
            because: "21.0 is not the literal 1.0");
    }

    [Fact]
    public void ReciprocalOfQuotient_MultiplicativeNumerator_IsFolded()
    {
        // `1.0 / (a * b / c)` — the last depth-0 multiplicative operator is the
        // division, so the group's root IS the division: fold to c / (a * b).
        const string src = """
#version 140

in vec4 in_var_TEXCOORD5;
out vec4 out_var_SV_Target;

void main()
{
    float _a = 1.0 / (in_var_TEXCOORD5.x * in_var_TEXCOORD5.y / in_var_TEXCOORD5.z);
    out_var_SV_Target = vec4(_a);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("((vTexCoord5.z) / (vTexCoord5.x * vTexCoord5.y))");
    }

    // ---- Issue #140: a round() nested inside another round()'s ARGUMENT. Rule 8
    // used to resume the scan past the whole replacement, so the inner call
    // survived as roundEven() — a WebGL1/Mesa load failure with exit code 0. ----

    [Fact]
    public void Round_NestedInsideAnotherRoundArgument_BothLowered_Issue140()
    {
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = texture(_10, vec2(roundEven(roundEven(in_var_TEXCOORD0.x * 7.0) * 0.5) * 0.25, in_var_TEXCOORD0.y));
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bround(Even)?\s*\(")
            .Should().BeFalse("the INNER nested round must be lowered too (issue #140)");
        result.Glsl.Should().Contain("floor((floor((vTexCoord0.x * 7.0) + 0.5) * 0.5) + 0.5)");

        int open  = result.Glsl.Count(c => c == '(');
        int close = result.Glsl.Count(c => c == ')');
        open.Should().Be(close, "the nested lowering must keep parentheses balanced");
    }

    // ---- Issue #137: the stage-agnostic body lowerings (Rules 8, 9b, 10, 11) must
    // run on the VERTEX stage too — a VS round() shipped roundEven() and a VS
    // early-return helper shipped the raw do{}while(false), both silent
    // Effect-load failures on Mesa / WebGL1 with compile exit 0. ----

    [Fact]
    public void VertexStage_Round_IsLoweredToFloorHalfUp_Issue137()
    {
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
    vec4 Tint;
} _Globals;

in vec4 in_var_POSITION0;
in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_COLOR0;
out vec2 out_var_TEXCOORD0;

void main()
{
    gl_Position = _Globals.WorldViewProjection * in_var_POSITION0;
    gl_Position.xy = roundEven(gl_Position.xy * 8.0) * 0.125;
    out_var_COLOR0 = in_var_COLOR0 * _Globals.Tint;
    out_var_TEXCOORD0 = in_var_TEXCOORD0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bround(Even)?\s*\(")
            .Should().BeFalse("Rule 8 must run on the vertex stage too (issue #137)");
        result.Glsl.Should().Contain("floor((gl_Position.xy * 8.0) + 0.5)");

        // The posFixup contract is untouched by the VS lowering.
        result.Glsl.Should().Contain("uniform vec4 posFixup;");
        result.Glsl.Should().Contain("gl_Position.y = gl_Position.y * posFixup.y;");
    }

    [Fact]
    public void VertexStage_PowSquareAndReciprocalOfQuotient_AreLowered_Issue137()
    {
        // Issue #137 made Rules 10 (pow-square) and 11 (reciprocal-fold) run on the vertex
        // stage as well, but every existing test for them passes ShaderStage.Pixel — so
        // deleting the two vertex-branch calls left the whole suite green. Pin both here.
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
    float Scale;
} _Globals;

in vec4 in_var_POSITION0;
out vec2 out_var_TEXCOORD0;

void main()
{
    gl_Position = _Globals.WorldViewProjection * in_var_POSITION0;
    float falloff = pow(gl_Position.w, 2.0);
    float inv = 1.0 / (_Globals.Scale / falloff);
    out_var_TEXCOORD0 = vec2(falloff, inv);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        // Rule 10: pow(x, 2.0) becomes the explicit multiply (the uniform is separately
        // rewritten into the vs_uniforms_vec4 register file, so match on shape not names).
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bpow\s*\([^,()]*,\s*2\.0\s*\)")
            .Should().BeFalse("Rule 10 must run on the vertex stage too (issue #137)");
        result.Glsl.Should().Contain("((gl_Position.w) * (gl_Position.w))");

        // Rule 11: 1.0 / (a / b) folds to b / a.
        result.Glsl.Should().NotContain("1.0 / (",
            "Rule 11 must run on the vertex stage too (issue #137)");
        result.Glsl.Should().MatchRegex(@"float inv = \(\(falloff\) / \(.+\)\);");
    }

    [Fact]
    public void VertexStage_OneShotDoWhile_IsLoweredToForLoop_Issue137()
    {
        // SPIRV-Cross's early-return wrapper in a VERTEX body (an inlined helper
        // with a conditional early return). Rule 9b must lower it to the
        // Appendix-A-allowed one-shot for; Rule 9a (break -> early `return;`)
        // must NOT run here — an early return would skip the posFixup tail.
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
    vec4 Tint;
} _Globals;

in vec4 in_var_POSITION0;
in vec4 in_var_COLOR0;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_COLOR0;
out vec2 out_var_TEXCOORD0;

void main()
{
    vec4 pos = _Globals.WorldViewProjection * in_var_POSITION0;
    do
    {
        if (pos.w <= 0.0)
        {
            break;
        }
        pos.xy += vec2(0.001, 0.001) * pos.w;
    } while(false);
    gl_Position = pos;
    out_var_COLOR0 = in_var_COLOR0 * _Globals.Tint;
    out_var_TEXCOORD0 = in_var_TEXCOORD0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"\bdo\s*\{")
            .Should().BeFalse("a raw do-while fails to load on WebGL1/KNI Reach (issues #107/#137)");
        result.Glsl.Should().NotContain("while(false)");
        result.Glsl.Should().NotContain("while (false)");
        System.Text.RegularExpressions.Regex
            .IsMatch(result.Glsl, @"for \(int \w+ = 0; \w+ < 1; \w+\+\+\)")
            .Should().BeTrue("Rule 9b lowers the one-shot wrapper to the Appendix-A for form");

        // The posFixup lines must sit AFTER the lowered loop, on the single
        // fall-through path — never inside it, never skippable by an early return.
        int loopIndex   = result.Glsl.IndexOf("for (int", StringComparison.Ordinal);
        int fixupIndex  = result.Glsl.IndexOf("gl_Position.y = gl_Position.y * posFixup.y;", StringComparison.Ordinal);
        loopIndex.Should().BeGreaterThan(0);
        fixupIndex.Should().BeGreaterThan(loopIndex, "the posFixup tail must run after the lowered loop");
        result.Glsl.Should().NotContain("\n    return;", "Rule 9a's early-return unwrap must stay pixel-only in a VS");
    }

    // ---- Issue #139: fragment shaders using derivative builtins must ship the
    // GL_OES_standard_derivatives header as the FIRST line (mgfxc parity;
    // strict ESSL 1.00 rejects derivative builtins without it). ----

    [Fact]
    public void PixelStage_DerivativeBuiltins_EmitStandardDerivativesHeaderFirst_Issue139()
    {
        const string src = """
#version 140

uniform sampler2D _10;
in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float w = fwidth(in_var_TEXCOORD0.x) + abs(dFdx(in_var_TEXCOORD0.y));
    out_var_SV_Target = texture(_10, in_var_TEXCOORD0) * vec4(w);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().StartWith("#extension GL_OES_standard_derivatives : enable\n",
            "mgfxc prepends the derivatives extension as the FIRST line (issue #139), and " +
            "fwidth counts too — SPIRV-Cross emits it directly");
    }

    [Fact]
    public void PixelStage_NoDerivatives_OmitsStandardDerivativesHeader_Issue139()
    {
        var result = MonoGameGlslRewriter.Rewrite(PixelatedRoundEven, ShaderStage.Pixel);

        result.Glsl.Should().NotContain("GL_OES_standard_derivatives",
            "the header is emitted only when a derivative builtin is present (mgfxc parity)");
    }

    // ---- Issue #138 (Rule 12), shape 2 — GaussianBlur-style: a constant-bounded for
    // loop with an EMPTY increment clause, the index instead advanced by the body's
    // last two statements (`<index>++; continue;` or `<index> += k; continue;`). GLSL
    // ES 1.00 Appendix A requires the increment in the header and forbids any other
    // write to the index, so this shape fails to load on WebGL1/KNI Reach (SD0402) and
    // independently makes `arr[base + index]` a non-constant-index-expression. ----

    [Fact]
    public void PixelStage_EmptyIncrementForLoop_IndexHoistedIntoHeader_Issue138()
    {
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    vec4 ps_uniforms_vec4[15];
} _Globals;

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    vec4 sum = vec4(0.0);
    for (int _40 = 0; _40 < 15; )
    {
        sum += _Globals.ps_uniforms_vec4[_40];
        _40++;
        continue;
    }
    out_var_SV_Target = sum;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().MatchRegex(@"for\s*\(int _40 = 0;\s*_40\s*<\s*15\s*;\s*_40\+\+\s*\)",
            "the increment hoists into the for-header, making it Appendix-A-legal");
        result.Glsl.Should().NotContain("_40++;\n        continue;",
            "the trailing increment+continue pair is removed once hoisted");
    }

    [Fact]
    public void PixelStage_EmptyIncrementForLoop_PlusEqualsVariant_IsHoisted_Issue138()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float sum = 0.0;
    for (int _12 = 0; _12 < 8; )
    {
        sum += float(_12);
        _12 += 2;
        continue;
    }
    out_var_SV_Target = vec4(sum);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().MatchRegex(@"for\s*\(int _12 = 0;\s*_12\s*<\s*8\s*;\s*_12\s*\+=\s*2\s*\)",
            "a `+= k` body increment hoists into the header the same way `++` does");
        result.Glsl.Should().NotContain("continue;");
    }

    [Fact]
    public void PixelStage_EmptyIncrementForLoop_OtherWriteToIndex_IsLeftUntouched_Issue138()
    {
        // A second write to the loop index elsewhere in the body means hoisting the
        // trailing increment into the header would change the iteration count — not
        // provably safe, so Rule 12 must decline and leave the loop exactly as emitted
        // (still flagged by SD0402, not silently mis-rewritten).
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float sum = 0.0;
    for (int _40 = 0; _40 < 15; )
    {
        if (sum > 4.0)
        {
            _40 += 3;
        }
        sum += float(_40);
        _40++;
        continue;
    }
    out_var_SV_Target = vec4(sum);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("_40++;\n        continue;",
            "a second write to the index makes hoisting unsafe, so the shape is left untouched");
        result.Glsl.Should().MatchRegex(@"for\s*\(int _40 = 0;\s*_40\s*<\s*15\s*;\s*\)",
            "the header keeps its empty increment clause");
    }

    [Fact]
    public void PixelStage_EmptyIncrementForLoop_OtherContinueElsewhere_IsLeftUntouched_Issue138()
    {
        // A second `continue` elsewhere in the body could skip the trailing increment
        // on some iterations — hoisting it into the header would change behavior, so
        // Rule 12 must decline.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float sum = 0.0;
    for (int _40 = 0; _40 < 15; )
    {
        if (sum > 4.0)
        {
            continue;
        }
        sum += float(_40);
        _40++;
        continue;
    }
    out_var_SV_Target = vec4(sum);
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        result.Glsl.Should().Contain("_40++;\n        continue;",
            "a second continue makes hoisting unsafe, so the shape is left untouched");
    }

    [Fact]
    public void VertexStage_EmptyIncrementForLoop_IsHoisted_Issue138()
    {
        // The stage-agnostic body lowerings (Rules 8-12) all run for the vertex stage
        // too (issue #137's lesson) — pin Rule 12 there as well.
        const string src = """
#version 140

layout(binding = 0, std140) uniform type_Globals
{
    mat4 WorldViewProjection;
} _Globals;

in vec4 in_var_POSITION0;
out float out_var_TEXCOORD0;

void main()
{
    gl_Position = _Globals.WorldViewProjection * in_var_POSITION0;
    float acc = 0.0;
    for (int _7 = 0; _7 < 4; )
    {
        acc += float(_7);
        _7++;
        continue;
    }
    out_var_TEXCOORD0 = acc;
}
""";
        var result = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Vertex);

        result.Glsl.Should().MatchRegex(@"for\s*\(int _7 = 0;\s*_7\s*<\s*4\s*;\s*_7\+\+\s*\)",
            "Rule 12 must run on the vertex stage too");
    }

    // ---- Issue #138 (Rule 13), shape 1 — Apos.Shapes' Newton-iteration style:
    // SPIRV-Cross emits a HEADER-LESS `for (;;)` whose body is a single
    // `if (idx < boundVar) { ...; idx++; continue; } else { ...; break; }`, with the
    // index declared as a separate statement just above and `boundVar` itself set,
    // just above THAT, from a compile-time-constant expression (a literal, or a
    // ternary between two literals) — the shader's true iteration ceiling is knowable,
    // it's just been renamed into a runtime-looking variable by SPIRV-Cross. Rewriting
    // to `for (int idx = 0; idx < <provenMax>; idx++) { if (idx < boundVar) {...} else
    // {...} }` is exact (not an approximation): <provenMax> IS the shader's real
    // maximum, so the loop can never run one iteration more or fewer than before. ----

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_TernaryLiteralBound_IsHoisted_Issue138()
    {
        // The exact shape found in the real, vendored apos-shapes.fx.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    bool _553 = in_var_TEXCOORD0.x > 0.0;
    int _555 = _553 ? 0 : 12;
    int _564 = 0;
    for (;;)
    {
        if (_564 < _555)
        {
            float _569 = float(_564) * 0.5;
            if (_569 < 0.001)
            {
                result = _569;
                break;
            }
            _564++;
            continue;
        }
        else
        {
            result = 1.0;
            break;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().NotContain("for (;;)", "the header-less loop must be given a real bound");
        rewritten.Glsl.Should().NotContain("int _564 = 0;\n    for",
            "the index declaration moves INTO the for-header, not stay as a separate statement");
        rewritten.Glsl.Should().MatchRegex(@"for\s*\(int _564 = 0;\s*_564\s*<=\s*12\s*;\s*_564\+\+\s*\)",
            "12 is _555's true maximum (the ternary's larger literal); the bound is <= so the terminal else stays reachable at the full trip count (issue #160)");
        rewritten.Glsl.Should().Contain("if (_564 < _555)",
            "the original runtime guard against the REAL (possibly smaller) bound must survive inside the loop");
        rewritten.Glsl.Should().NotContain("_564++;\n            continue;",
            "the trailing increment+continue is hoisted into the header, not left duplicated in the body");
    }

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_ElseBranchStillReachableAtMaxTripCount_Issue160()
    {
        // Regression for issue #160. This is the exact apos-shapes.fx EllipseSDF shape:
        // `_555` is the runtime trip count (0 in the degenerate case, else 12), and the
        // loop's `else` branch is the FINALIZER that assigns the phi output `_604` on the
        // path where the loop runs to its ceiling without the inner convergence break.
        //
        // The original `for (;;)` runs that else at _564 == _555. When _555 == 12 (the
        // eccentric-ellipse case, which needs the full Newton budget), a rewrite to
        // `for (_564 = 0; _564 < 12; _564++)` exits at _564 == 12 WITHOUT running the
        // else, leaving `_604` read uninitialized downstream. The header bound must let
        // _564 REACH 12 so the finalizer still executes — otherwise thin ellipses render
        // from garbage distances (0.14.0's GL-only regression).
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float _604;
    bool _553 = in_var_TEXCOORD0.x > 0.0;
    int _555 = _553 ? 0 : 12;
    float _562 = 0.5;
    int _564 = 0;
    for (;;)
    {
        if (_564 < _555)
        {
            float _563 = _562 + 0.1;
            if (_563 < 0.001)
            {
                _604 = _563;
                break;
            }
            _562 = _563;
            _564++;
            continue;
        }
        else
        {
            _604 = _562;
            break;
        }
    }
    out_var_SV_Target = vec4(_604);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().NotContain("for (;;)", "the header-less loop must be given a real bound");
        // The finalizer `else { _604 = _562; }` must still be reachable at the full trip
        // count, i.e. _564 must be able to equal 12 inside the loop. `_564 < 12` drops it;
        // `_564 <= 12` (or `_564 < 13`) keeps it.
        rewritten.Glsl.Should().MatchRegex(
            @"for\s*\(int _564 = 0;\s*_564\s*(<=\s*12|<\s*13)\s*;\s*_564\+\+\s*\)",
            "the terminal else-branch finalizer must remain reachable when the loop runs its full ceiling");
        rewritten.Glsl.Should().Contain("_604 = _562;",
            "the else-branch finalizer that assigns the phi output must survive the rewrite");
    }

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_PlainLiteralBound_IsHoisted_Issue138()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    int _30 = 8;
    int _31 = 0;
    for (;;)
    {
        if (_31 < _30)
        {
            result += float(_31);
            _31++;
            continue;
        }
        else
        {
            result = 0.0;
            break;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().MatchRegex(@"for\s*\(int _31 = 0;\s*_31\s*<=\s*8\s*;\s*_31\+\+\s*\)",
            "a plain literal bound (no ternary) hoists the same way; <= keeps the terminal else reachable (issue #160)");
    }

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_NonLiteralBound_IsLeftUntouched_Issue138()
    {
        // The bound comes from a computed, non-literal expression with no
        // compile-time-provable ceiling — there is no safe constant to put in the
        // header, so Rule 13 must decline (SD0402 keeps warning, which is the honest
        // outcome here).
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    int _30 = int(in_var_TEXCOORD0.x * 100.0);
    int _31 = 0;
    for (;;)
    {
        if (_31 < _30)
        {
            result += float(_31);
            _31++;
            continue;
        }
        else
        {
            result = 0.0;
            break;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().Contain("for (;;)",
            "a non-literal bound has no provable ceiling, so the loop must be left untouched");
    }

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_OtherWriteToIndexInTrueBranch_IsLeftUntouched_Issue138()
    {
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    int _30 = 8;
    int _31 = 0;
    for (;;)
    {
        if (_31 < _30)
        {
            if (result > 4.0)
            {
                _31 += 3;
            }
            result += float(_31);
            _31++;
            continue;
        }
        else
        {
            result = 0.0;
            break;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().Contain("for (;;)",
            "a second write to the index makes hoisting unsafe, so the shape is left untouched");
    }

    [Fact]
    public void PixelStage_BoundedHeaderlessForLoop_FalseBranchDoesNotEndInBreak_IsLeftUntouched_Issue138()
    {
        // An unexpected shape (SPIRV-Cross always emits a trailing break here in
        // practice) — Rule 13 must not guess at a rewrite for anything else.
        const string src = """
#version 140

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    float result;
    int _30 = 8;
    int _31 = 0;
    for (;;)
    {
        if (_31 < _30)
        {
            result += float(_31);
            _31++;
            continue;
        }
        else
        {
            result = 0.0;
        }
    }
    out_var_SV_Target = vec4(result);
}
""";
        var rewritten = MonoGameGlslRewriter.Rewrite(src, ShaderStage.Pixel);

        rewritten.Glsl.Should().Contain("for (;;)",
            "the false branch must end in break for this rewrite to be provably safe");
    }
}
