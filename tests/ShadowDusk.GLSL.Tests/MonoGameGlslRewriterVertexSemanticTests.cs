#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// GL vertex-input semantics beyond POSITION / COLOR / TEXCOORD / NORMAL, and matrix
/// interpolants (KNI EffectTest's <c>DeferredBasicEffect</c>: <c>BINORMAL0</c> was SD0210, then
/// its <c>float3x3 : TEXCOORD2</c> interpolant was). The usage bytes are mgfxc 3.8.4.1
/// <c>/Profile:OpenGL</c>'s, measured per semantic; the attribute-record goldens and the real-engine
/// renders are in <c>tests/fixtures/golden/vertex-semantics/</c>.
/// </summary>
public sealed class MonoGameGlslRewriterVertexSemanticTests
{
    private static string Vs(string inputType, string identifier) => $$"""
#version 140
in vec4 in_var_POSITION0;
in {{inputType}} {{identifier}};
out vec4 out_var_COLOR0;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_COLOR0 = vec4({{identifier}}{{(inputType == "vec4" ? "" : ".x")}});
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";

    // MonoGame VertexElementUsage: Position=0, Color=1, TextureCoordinate=2, Normal=3, Binormal=4,
    // Tangent=5, BlendIndices=6, BlendWeight=7, Depth=8, Fog=9, PointSize=10. Every row was measured
    // against mgfxc 3.8.4.1 /Profile:OpenGL (2026-10-28), which writes exactly these usage + index bytes.
    [Theory]
    [InlineData("vec3", "in_var_BINORMAL0",     4, 0)]
    [InlineData("vec3", "in_var_BINORMAL2",     4, 2)]
    [InlineData("vec3", "in_var_TANGENT0",      5, 0)]
    [InlineData("vec3", "in_var_TANGENT7",      5, 7)]
    [InlineData("vec4", "in_var_BLENDINDICES0", 6, 0)]
    [InlineData("vec4", "in_var_BLENDINDICES1", 6, 1)]
    [InlineData("vec4", "in_var_BLENDWEIGHT0",  7, 0)]
    [InlineData("vec4", "in_var_BLENDWEIGHT1",  7, 1)]
    [InlineData("float", "in_var_DEPTH0",       8, 0)]
    [InlineData("float", "in_var_DEPTH1",       8, 1)]
    [InlineData("float", "in_var_FOG0",         9, 0)]
    [InlineData("float", "in_var_FOG1",         9, 1)]
    [InlineData("float", "in_var_PSIZE0",      10, 0)]
    [InlineData("float", "in_var_PSIZE1",      10, 1)]
    [InlineData("vec2", "in_var_TEXCOORD3",     2, 3)]
    [InlineData("vec4", "in_var_POSITION1",     0, 1)]
    [InlineData("vec4", "in_var_COLOR2",        1, 2)]
    [InlineData("vec3", "in_var_NORMAL1",       3, 1)]
    [InlineData("vec3", "in_var_Binormal0",     4, 0)]   // DXC mirrors the author's spelling
    [InlineData("vec3", "in_var_tangent1",      5, 1)]
    [InlineData("vec4", "in_var_BLENDWEIGHT",   7, 0)]   // an omitted index is index 0
    public void VertexInput_MapsToMgfxcUsageAndIndex(string type, string identifier, byte usage, byte index)
    {
        var result = MonoGameGlslRewriter.Rewrite(Vs(type, identifier), ShaderStage.Vertex);

        result.Attributes.Count.ShouldBe(2);
        result.Attributes[0].Usage.ShouldBe((byte)0);
        result.Attributes[1].Name.ShouldBe("vs_v1");
        result.Attributes[1].Usage.ShouldBe(usage);
        result.Attributes[1].Index.ShouldBe(index);
        result.Glsl.ShouldContain("attribute vec4 vs_v1;", Case.Sensitive);
        result.Glsl.ShouldNotContain(identifier, Case.Sensitive);
    }

    [Theory]
    [InlineData("BINORMAL3")]
    [InlineData("TANGENT0")]
    [InlineData("BLENDINDICES2")]
    [InlineData("BLENDWEIGHT1")]
    [InlineData("DEPTH0")]
    [InlineData("FOG4")]
    [InlineData("PSIZE0")]
    [InlineData("TEXCOORD12")]
    [InlineData("position")]
    public void VertexInput_UsesTheSharedMapper(string semantic)
    {
        // One table: the GL attribute record is exactly what Vulkan's and DirectX 12's
        // attribute tables get from VertexSemanticMapper.
        var result = MonoGameGlslRewriter.Rewrite(Vs("vec4", "in_var_" + semantic), ShaderStage.Vertex);
        var (usage, index) = VertexSemanticMapper.Map(semantic, out bool recognized);

        recognized.ShouldBeTrue();
        result.Attributes[1].Usage.ShouldBe(usage);
        ((int)result.Attributes[1].Index).ShouldBe(index);
    }

    [Theory]
    [InlineData("in_var_MYSEM0")]       // mgfxc's fxc vs_3_0 refuses it too (X4502)
    [InlineData("in_var_POSITIONT")]    // a table name followed by a non-numeric tail
    [InlineData("in_var_TEXCOORD256")]  // the record carries the index in one byte
    [InlineData("in_var_SAMPLE0")]      // mgfxc maps it (usage 11); not modelled, so loud
    public void VertexInput_UnmodelledSemantic_FailsLoudly(string identifier)
    {
        var act = () => MonoGameGlslRewriter.Rewrite(Vs("vec4", identifier), ShaderStage.Vertex);

        string message = Should.Throw<MonoGameGlslRewriteException>(act).Message;
        message.ShouldContain(identifier["in_var_".Length..].ToUpperInvariant(), Case.Sensitive);
        message.ShouldContain("BINORMAL / TANGENT / BLENDINDICES / BLENDWEIGHT", Case.Sensitive);
    }

    // ---- Matrix interpolants (Rule 4c) ----

    // Verbatim SPIRV-Cross shape for `float3x3 Frame : TEXCOORD2` written by a vertex shader.
    private const string MatrixVs = """
#version 140
in vec4 in_var_POSITION0;
in vec3 in_var_NORMAL0;
in vec3 in_var_BINORMAL0;
in vec3 in_var_TANGENT0;
out vec2 out_var_TEXCOORD0;
out mat3 out_var_TEXCOORD2;
out vec4 out_var_TEXCOORD5;

void main()
{
    gl_Position = in_var_POSITION0;
    out_var_TEXCOORD0 = vec2(0.25, 0.75);
    out_var_TEXCOORD2 = mat3(in_var_TANGENT0, in_var_BINORMAL0, in_var_NORMAL0);
    out_var_TEXCOORD5 = vec4(in_var_NORMAL0.zyx, 1.0);
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";

    private const string MatrixPs = """
#version 140
in vec2 in_var_TEXCOORD0;
in mat3 in_var_TEXCOORD2;
in vec4 in_var_TEXCOORD5;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = vec4(in_var_TEXCOORD2 * vec3(0.5, 0.25, 0.125), in_var_TEXCOORD2[1].z) + in_var_TEXCOORD5;
}
""";

    [Fact]
    public void MatrixInterpolant_VertexStage_SplitsIntoOneTexCoordPerColumn_LikeFxc()
    {
        var result = MonoGameGlslRewriter.Rewrite(MatrixVs, ShaderStage.Vertex);
        string glsl = result.Glsl;

        // TEXCOORD2..4: three registers, then TEXCOORD5 is a separate interpolant.
        glsl.ShouldContain("varying vec4 vTexCoord2;\nvarying vec4 vTexCoord3;\nvarying vec4 vTexCoord4;\n", Case.Sensitive);
        glsl.ShouldContain("varying vec4 vTexCoord5;", Case.Sensitive);
        glsl.ShouldContain("mat3 vs_oT2_mat;", Case.Sensitive);
        glsl.ShouldContain("vs_oT2_mat = mat3(vs_v3.xyz, vs_v2.xyz, vs_v1.xyz);", Case.Sensitive);

        // fxc packs a column_major matrix interpolant one HLSL COLUMN per register (mgfxc's
        // golden: vs_o2 = (Tangent.x, Binormal.x, Normal.x)); SPIRV-Cross's mat3 columns are the
        // HLSL rows, so register k is the GLSL row k.
        glsl.ShouldContain("vTexCoord2.xyz = vec3(vs_oT2_mat[0][0], vs_oT2_mat[1][0], vs_oT2_mat[2][0]);", Case.Sensitive);
        glsl.ShouldContain("vTexCoord3.xyz = vec3(vs_oT2_mat[0][1], vs_oT2_mat[1][1], vs_oT2_mat[2][1]);", Case.Sensitive);
        glsl.ShouldContain("vTexCoord4.xyz = vec3(vs_oT2_mat[0][2], vs_oT2_mat[1][2], vs_oT2_mat[2][2]);", Case.Sensitive);

        // The copy-out runs after every body write, before the position fixup at the end of main.
        glsl.IndexOf("vTexCoord4.xyz =", StringComparison.Ordinal)
            .ShouldBeLessThan(glsl.IndexOf("gl_Position.y = gl_Position.y * posFixup.y;", StringComparison.Ordinal));
        glsl.IndexOf("vs_oT2_mat = mat3(", StringComparison.Ordinal)
            .ShouldBeLessThan(glsl.IndexOf("vTexCoord2.xyz =", StringComparison.Ordinal));
        glsl.ShouldNotContain("out_var_", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_PixelStage_RebuildsTheMatrixFromItsRegisters()
    {
        var result = MonoGameGlslRewriter.Rewrite(MatrixPs, ShaderStage.Pixel);
        string glsl = result.Glsl;

        glsl.ShouldContain("varying vec4 vTexCoord2;\nvarying vec4 vTexCoord3;\nvarying vec4 vTexCoord4;\n", Case.Sensitive);
        // m[c][k] = vTexCoord{2+k}[c]: the inverse of the vertex stage's copy-out.
        const string rebuilt = "mat3(vec3(vTexCoord2.x, vTexCoord3.x, vTexCoord4.x), " +
                               "vec3(vTexCoord2.y, vTexCoord3.y, vTexCoord4.y), " +
                               "vec3(vTexCoord2.z, vTexCoord3.z, vTexCoord4.z))";
        glsl.ShouldContain(rebuilt + " * vec3(0.5, 0.25, 0.125)", Case.Sensitive);
        glsl.ShouldContain(rebuilt + "[1].z", Case.Sensitive);
        glsl.ShouldNotContain("in_var_", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_NonSquare_UsesOneRegisterPerGlslRow()
    {
        // HLSL float2x4 -> GLSL mat2x4 (2 columns of vec4): four registers of two components.
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
out mat2x4 out_var_TEXCOORD1;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_TEXCOORD1 = mat2x4(in_var_POSITION0, in_var_POSITION0.wzyx);
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        string glsl = MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex).Glsl;

        glsl.ShouldContain("mat2x4 vs_oT1_mat;", Case.Sensitive);
        glsl.ShouldContain("vTexCoord1.xy = vec2(vs_oT1_mat[0][0], vs_oT1_mat[1][0]);", Case.Sensitive);
        glsl.ShouldContain("vTexCoord4.xy = vec2(vs_oT1_mat[0][3], vs_oT1_mat[1][3]);", Case.Sensitive);
        glsl.ShouldNotContain("vTexCoord5", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_NonTexCoordSemantic_FailsLoudly()
    {
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
out mat3 out_var_COLOR0;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_COLOR0 = mat3(1.0);
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex);
        Should.Throw<MonoGameGlslRewriteException>(act).Message.ShouldContain("only a TEXCOORD semantic", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_OverlappingAnotherInterpolant_FailsLoudly()
    {
        // float3x3 at TEXCOORD2 occupies TEXCOORD2..4, so a TEXCOORD3 beside it would alias.
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
out mat3 out_var_TEXCOORD2;
out vec4 out_var_TEXCOORD3;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_TEXCOORD2 = mat3(1.0);
    out_var_TEXCOORD3 = in_var_POSITION0;
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex);
        Should.Throw<MonoGameGlslRewriteException>(act).Message.ShouldContain("vTexCoord3", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_VertexStageWithEarlyReturn_FailsLoudly()
    {
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
out mat3 out_var_TEXCOORD0;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_TEXCOORD0 = mat3(1.0);
    if (in_var_POSITION0.x > 0.0)
    {
        return;
    }
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex);
        Should.Throw<MonoGameGlslRewriteException>(act).Message.ShouldContain("'return'", Case.Sensitive);
    }

    [Fact]
    public void MatrixInterpolant_TemporaryNameCollision_FailsLoudly()
    {
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
out mat3 out_var_TEXCOORD2;
void main()
{
    vec4 vs_oT2_mat = in_var_POSITION0;
    gl_Position = vs_oT2_mat;
    out_var_TEXCOORD2 = mat3(1.0);
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex);
        Should.Throw<MonoGameGlslRewriteException>(act).Message.ShouldContain("vs_oT2_mat", Case.Sensitive);
    }

    [Fact]
    public void MatrixVertexInput_IsStillRefusedLoudly()
    {
        // A matrix vertex ATTRIBUTE (float4x4 instance transform as one input) is not modelled.
        const string vs = """
#version 140
in mat4 in_var_TEXCOORD1;
in vec4 in_var_POSITION0;
void main()
{
    gl_Position = in_var_TEXCOORD1 * in_var_POSITION0;
}
""";
        var act = () => MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex);
        Should.Throw<MonoGameGlslRewriteException>(act).Message.ShouldContain("in_var_TEXCOORD1", Case.Sensitive);
    }
}
