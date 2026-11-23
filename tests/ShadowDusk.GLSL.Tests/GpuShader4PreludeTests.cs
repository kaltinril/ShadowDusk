#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Rule 19 (issue #524): a stage using an integer construct the versionless dialect lacks gets a
/// guarded <c>GL_EXT_gpu_shader4</c> prelude, and every other stage stays byte-identical. Which
/// stacks accept the prelude is measured by the real-engine gate
/// (<c>validation/VertexSemanticsGl</c>, the <c>integer dialect</c> rows); these tests pin the text.
/// </summary>
public sealed class GpuShader4PreludeTests
{
    private const string Guard = "#if !defined(GL_ES) && __VERSION__ < 130 && defined(GL_EXT_gpu_shader4)\n";
    private const string Extension = "#extension GL_EXT_gpu_shader4 : enable\n";
    private const string UintAlias = "#define uint unsigned int\n";

    private static string Fragment(string statement) =>
        $"varying vec4 vTexCoord0;\n\nvoid main()\n{{\n    int i = int(vTexCoord0.x);\n    {statement}\n}}\n";

    [Theory]
    [InlineData("gl_FragColor = vec4(float(i));")]
    [InlineData("if ((i > 0 && i < 4) || (i > 8 ^^ i < 2)) discard;")]
    [InlineData("gl_FragColor = vec4(float(i) * 1.5e2);")]
    public void StageWithoutAnIntegerConstruct_IsTheSameString(string statement)
    {
        string glsl = Fragment(statement);
        GpuShader4Prelude.Apply(glsl).ShouldBeSameAs(glsl);
    }

    [Theory]
    [InlineData("int v = gl_VertexID;")]
    [InlineData("int v = gl_InstanceID;")]
    [InlineData("uvec2 v = uvec2(i);")]
    [InlineData("int v = i << 1;")]
    [InlineData("int v = i >> 1;")]
    [InlineData("int v = i & 1;")]
    [InlineData("int v = i | 1;")]
    [InlineData("int v = i ^ 1;")]
    [InlineData("int v = ~i;")]
    [InlineData("int v = i % 2;")]
    [InlineData("float v = float(3u);")]
    [InlineData("float v = float(0xFFu);")]
    public void IntegerConstructWithoutTheUintKeyword_GetsThePreludeWithNoAlias(string statement)
    {
        string glsl = Fragment(statement);
        GpuShader4Prelude.Apply(glsl).ShouldBe(Guard + Extension + "#endif\n" + glsl);
    }

    [Fact]
    public void UintKeyword_GetsTheAlias()
    {
        string glsl = Fragment("uint v = uint(i);");
        GpuShader4Prelude.Apply(glsl).ShouldBe(Guard + Extension + UintAlias + "#endif\n" + glsl);
    }

    [Fact]
    public void IdentifierContainingUint_IsNotTheKeyword()
    {
        string glsl = Fragment("float uint_scale = 2.0; float my_uint = uint_scale;");
        GpuShader4Prelude.Apply(glsl).ShouldBeSameAs(glsl);
    }

    [Fact]
    public void SpirvCrossExtensionBlock_IsHoistedAheadOfEveryDeclaration()
    {
        // The gl_InstanceID shape: Mesa refuses "#extension directive in the middle of a shader".
        const string block =
            "#ifdef GL_ARB_shader_draw_parameters\n#extension GL_ARB_shader_draw_parameters : enable\n#endif\n";
        const string before = "uniform vec4 posFixup;\n\n";
        const string after =
            "attribute vec4 vs_v0;\n" +
            "#ifdef GL_ARB_shader_draw_parameters\n#define SPIRV_Cross_BaseInstance gl_BaseInstanceARB\n#else\n" +
            "uniform int SPIRV_Cross_BaseInstance;\n#endif\n\n" +
            "void main()\n{\n    gl_Position = vs_v0 * float(gl_InstanceID + SPIRV_Cross_BaseInstance);\n}\n";

        GpuShader4Prelude.Apply(before + block + after)
            .ShouldBe(Guard + Extension + "#endif\n" + block + before + after);
    }

    [Fact]
    public void BareExtensionLine_IsHoistedAheadOfEveryDeclaration()
    {
        // The asuint shape: posFixup lands above SPIRV-Cross's unguarded #extension.
        const string line = "#extension GL_ARB_shader_bit_encoding : require\n";
        const string before = "uniform vec4 posFixup;\n\n";
        const string after = "void main()\n{\n    uint u = floatBitsToUint(posFixup.x);\n}\n";

        GpuShader4Prelude.Apply(before + line + after)
            .ShouldBe(Guard + Extension + UintAlias + "#endif\n" + line + before + after);
    }

    [Fact]
    public void ExtensionInsideAnotherConditional_StaysWhereItIs()
    {
        const string body =
            "#if __VERSION__ >= 300\n#define texture2DLod textureLod\n" +
            "#elif defined(GL_ARB_shader_texture_lod)\n#extension GL_ARB_shader_texture_lod : enable\n#endif\n" +
            "void main()\n{\n    int v = 1 << 2;\n}\n";

        GpuShader4Prelude.Apply(body).ShouldBe(Guard + Extension + "#endif\n" + body);
    }

    [Theory]
    [InlineData("findMSB")]
    [InlineData("bitCount")]
    public void Analyzer_IntegerBuiltinTheExtensionLacks_IsNamedAsUnlowered(string builtin)
    {
        string glsl = GpuShader4Prelude.Apply(Fragment($"gl_FragColor = vec4(float({builtin}(i)));"));

        GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Pixel, passHasVertexShader: true, "t.fx", "PS")
            .ShouldContain(f => f.Code == "SD0403" && f.Message.Contains($"'{builtin}()'", StringComparison.Ordinal)
                                && f.Message.Contains("No ShadowDusk lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_VertexStageFromTheIssue_StartsWithThePrelude()
    {
        const string spirvCross =
            "#version 140\n\nvoid main()\n{\n" +
            "    uint _22 = uint(int(uint(gl_VertexID)));\n" +
            "    gl_Position = vec4(float((_22 << 1u) & 2u) * 2.0 + (-1.0), float(_22 & 2u) * 2.0 + (-1.0), 0.0, 1.0);\n" +
            "}\n";

        string glsl = MonoGameGlslRewriter.Rewrite(spirvCross, ShaderStage.Vertex).Glsl;

        glsl.ShouldStartWith(Guard + Extension + UintAlias + "#endif\n#ifdef GL_ES\n", Case.Sensitive);
        // The body keeps SPIRV-Cross's spelling: the macro does the respelling, under the guard only.
        glsl.ShouldContain("uint _22 = uint(int(uint(gl_VertexID)));", Case.Sensitive);
    }

    [Fact]
    public void Rewrite_PixelStageWithDerivatives_KeepsEveryExtensionAheadOfTheFirstToken()
    {
        const string spirvCross =
            "#version 140\n\nin vec2 in_var_TEXCOORD0;\nout vec4 out_var_SV_Target;\n\nvoid main()\n{\n" +
            "    uint u = uint(in_var_TEXCOORD0.x);\n" +
            "    out_var_SV_Target = vec4(float(u), dFdx(in_var_TEXCOORD0.x), 0.0, 1.0);\n" +
            "}\n";

        string glsl = MonoGameGlslRewriter.Rewrite(spirvCross, ShaderStage.Pixel).Glsl;

        glsl.ShouldStartWith(
            Guard + Extension + UintAlias + "#endif\n#extension GL_OES_standard_derivatives : enable\n#ifdef GL_ES\n",
            Case.Sensitive);
    }

    [Fact]
    public void Rewrite_StageWithoutAnIntegerConstruct_HasNoPrelude()
    {
        const string spirvCross =
            "#version 140\n\nin vec2 in_var_TEXCOORD0;\nout vec4 out_var_SV_Target;\n\nvoid main()\n{\n" +
            "    out_var_SV_Target = vec4(in_var_TEXCOORD0, 0.0, 1.0);\n}\n";

        string glsl = MonoGameGlslRewriter.Rewrite(spirvCross, ShaderStage.Pixel).Glsl;

        glsl.ShouldNotContain("gpu_shader4", Case.Sensitive);
        glsl.ShouldStartWith("#ifdef GL_ES\n", Case.Sensitive);
    }

    [Fact]
    public void Analyzer_IntOnlyStage_ReportsNoUnsignedTypeFromThePrelude()
    {
        string glsl = GpuShader4Prelude.Apply(Fragment("gl_FragColor = vec4(float(i << 1));"));

        var findings = GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Pixel, passHasVertexShader: true, "t.fx", "PS");

        findings.ShouldContain(f => f.Code == "SD0403" && f.Message.Contains("shift", StringComparison.Ordinal));
        findings.ShouldNotContain(f => f.Message.Contains("unsigned integer", StringComparison.Ordinal));
        // The guard's own && is not an integer operator.
        findings.ShouldNotContain(f => f.Message.Contains("bitwise", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_VertexId_WarnsThatEs100HasNoEquivalent()
    {
        string glsl = GpuShader4Prelude.Apply(
            "void main()\n{\n    gl_Position = vec4(float(gl_VertexID));\n}\n");

        var findings = GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Vertex, passHasVertexShader: true, "t.fx", "VS");

        var finding = findings.Single(f => f.Code == "SD0403");
        finding.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        finding.Message.ShouldContain("gl_VertexID", Case.Sensitive);
        finding.Message.ShouldContain("GL_EXT_gpu_shader4", Case.Sensitive);
        finding.Message.ShouldContain("GLSL ES 1.00", Case.Sensitive);
    }

    [Theory]
    [InlineData("gl_VertexID")]
    [InlineData("gl_InstanceID")]
    public void Analyzer_IdDrivenVertexShaderWithNoAttribute_WarnsSd0404(string id)
    {
        string glsl = GpuShader4Prelude.Apply($"void main()\n{{\n    gl_Position = vec4(float({id}));\n}}\n");

        var findings = GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Vertex, passHasVertexShader: true, "t.fx", "VS");

        var finding = findings.Single(f => f.Code == "SD0404");
        finding.Severity.ShouldBe(ShaderErrorSeverity.Warning);
        finding.Message.ShouldStartWith("Vertex shader 'VS'", Case.Sensitive);
        finding.Message.ShouldContain("Apple's OpenGL 2.1", Case.Sensitive);
    }

    [Fact]
    public void Analyzer_IdDrivenVertexShaderWithAnAttribute_HasNoSd0404()
    {
        string glsl = GpuShader4Prelude.Apply(
            "attribute vec4 vs_v0;\n\nvoid main()\n{\n    gl_Position = vs_v0 * float(gl_VertexID);\n}\n");

        GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Vertex, passHasVertexShader: true, "t.fx", "VS")
            .ShouldNotContain(f => f.Code == "SD0404");
    }

    [Fact]
    public void Analyzer_VertexShaderWithoutAnId_HasNoSd0404()
    {
        const string glsl = "void main()\n{\n    gl_Position = vec4(0.0, 0.0, 0.0, 1.0);\n}\n";

        GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Vertex, passHasVertexShader: true, "t.fx", "VS")
            .ShouldNotContain(f => f.Code == "SD0404");
    }
}
