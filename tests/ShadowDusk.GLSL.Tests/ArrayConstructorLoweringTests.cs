#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Rule 18 (issue #475): an HLSL array initializer reaches SPIRV-Cross's output as an array
/// constructor (<c>float[](…)</c>), which is GLSL 1.20 / ESSL 3.00. The versionless dialect
/// (GLSL 1.10 / ESSL 1.00) has none, so WebGL1 and KNI Reach refuse the shader at Effect load.
/// The lowering declares the array without an initializer and assigns each element.
/// </summary>
public sealed class ArrayConstructorLoweringTests
{
    private static string Lower(string body) => ArrayConstructorLowering.Lower(body);

    [Fact]
    public void ShaderWithoutAnArrayConstructor_IsReturnedUnchanged()
    {
        const string body = "uniform vec4 u[4];\n\nvoid main()\n{\n    gl_FragColor = u[1] * u[2];\n}\n";
        Lower(body).ShouldBeSameAs(body);
    }

    [Fact]
    public void GlobalConstArray_BecomesAPlainArrayAssignedAtTheStartOfMain()
    {
        // The exact shape from issue #475: DXC hoists the lookup table to a module-level const.
        const string body =
            "float _41;\n" +
            "const float _42[4] = float[](0.0, 8.0, 2.0, 10.0);\n" +
            "varying vec4 vTexCoord0;\n" +
            "\n" +
            "void main()\n" +
            "{\n" +
            "    gl_FragColor = vec4(_42[int(vTexCoord0.x)]);\n" +
            "}\n";

        string lowered = Lower(body);

        lowered.ShouldNotContain("float[](", Case.Sensitive);
        lowered.ShouldNotContain("const float _42", Case.Sensitive);
        lowered.ShouldContain("\nfloat _42[4];\n", Case.Sensitive);
        // The assignments come before the first statement of main.
        int open = lowered.IndexOf("void main()", StringComparison.Ordinal);
        int a0 = lowered.IndexOf("_42[0] = 0.0;", StringComparison.Ordinal);
        int a3 = lowered.IndexOf("_42[3] = 10.0;", StringComparison.Ordinal);
        int use = lowered.IndexOf("gl_FragColor", StringComparison.Ordinal);
        a0.ShouldBeGreaterThan(open);
        a3.ShouldBeGreaterThan(a0);
        use.ShouldBeGreaterThan(a3);
    }

    [Fact]
    public void LocalArray_IsDeclaredThenAssignedInPlace()
    {
        const string body =
            "varying vec4 vTexCoord0;\n" +
            "\n" +
            "void main()\n" +
            "{\n" +
            "    float _20[2] = float[](vTexCoord0.x, vTexCoord0.y);\n" +
            "    gl_FragColor = vec4(_20[1]);\n" +
            "}\n";

        string lowered = Lower(body);

        lowered.ShouldNotContain("float[](", Case.Sensitive);
        lowered.ShouldContain("    float _20[2];\n    _20[0] = vTexCoord0.x;\n    _20[1] = vTexCoord0.y;\n", Case.Sensitive);
    }

    [Fact]
    public void ElementsWithNestedCommas_AreSplitAtTheTopLevelOnly()
    {
        const string body =
            "const vec2 _7[2] = vec2[](vec2(1.0, 2.0), vec2(3.0, 4.0));\n" +
            "\n" +
            "void main()\n" +
            "{\n" +
            "    gl_FragColor = vec4(_7[1], _7[0]);\n" +
            "}\n";

        string lowered = Lower(body);

        lowered.ShouldContain("_7[0] = vec2(1.0, 2.0);", Case.Sensitive);
        lowered.ShouldContain("_7[1] = vec2(3.0, 4.0);", Case.Sensitive);
    }

    [Fact]
    public void SizedConstructorSpelling_IsLoweredToo()
    {
        const string body =
            "const float _5[3] = float[3](1.0, 2.0, 3.0);\n\nvoid main()\n{\n    gl_FragColor = vec4(_5[2]);\n}\n";

        string lowered = Lower(body);

        lowered.ShouldNotContain("float[3](", Case.Sensitive);
        lowered.ShouldContain("_5[2] = 3.0;", Case.Sensitive);
    }

    [Fact]
    public void StructArray_IsLowered()
    {
        const string body =
            "struct S\n{\n    float a;\n    float b;\n};\n\n" +
            "const S _9[2] = S[](S(1.0, 2.0), S(3.0, 4.0));\n\n" +
            "void main()\n{\n    gl_FragColor = vec4(_9[1].a);\n}\n";

        string lowered = Lower(body);

        lowered.ShouldNotContain("S[](", Case.Sensitive);
        lowered.ShouldContain("_9[1] = S(3.0, 4.0);", Case.Sensitive);
    }

    [Fact]
    public void GlobalArray_UsedByAnotherFunction_IsStillInitializedBeforeIt()
    {
        // A non-entry function reads the global; main (its only caller) assigns first.
        const string body =
            "const float _3[2] = float[](5.0, 6.0);\n\n" +
            "float pick(int i)\n{\n    return _3[i];\n}\n\n" +
            "void main()\n{\n    gl_FragColor = vec4(pick(1));\n}\n";

        string lowered = Lower(body);

        lowered.ShouldContain("float _3[2];", Case.Sensitive);
        lowered.IndexOf("_3[0] = 5.0;", StringComparison.Ordinal)
            .ShouldBeGreaterThan(lowered.IndexOf("void main()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("const float _1[2][2] = float[][](float[](1.0, 2.0), float[](3.0, 4.0));")]      // nested array
    [InlineData("const float _1[3] = float[](1.0, 2.0);")]                                        // count mismatch
    public void UnmodelledShape_DeclinesAndLeavesTheTextUnchanged(string declaration)
    {
        // All-or-nothing: the SD0403 backstop must still see the surviving constructor.
        string body = declaration + "\n\nvoid main()\n{\n    gl_FragColor = vec4(_1[0]);\n}\n";
        Lower(body).ShouldBe(body);
    }

    [Fact]
    public void GlobalArray_UsedInAnotherGlobalInitializer_DeclinesAndLeavesTheTextUnchanged()
    {
        // `const float k = _3[1];` needs _3 to be constant; once lowered it is not.
        const string body =
            "const float _3[2] = float[](5.0, 6.0);\nconst float _4 = _3[1];\n\n" +
            "void main()\n{\n    gl_FragColor = vec4(_4);\n}\n";
        Lower(body).ShouldBe(body);
    }

    [Fact]
    public void Rewrite_PixelStage_IssueRepro_EmitsNoArrayConstructor()
    {
        // SPIRV-Cross output for the issue-#475 shader, as the rewriter receives it.
        const string spirvCross = """
#version 140
#ifdef GL_ARB_shading_language_420pack
#extension GL_ARB_shading_language_420pack : require
#endif

float _41;
const float _42[4] = float[](0.0, 8.0, 2.0, 10.0);

in vec2 in_var_TEXCOORD0;
out vec4 out_var_SV_Target;

void main()
{
    out_var_SV_Target = vec4(_42[int(in_var_TEXCOORD0.x * 3.0)] * 0.0625);
}
""";

        var result = MonoGameGlslRewriter.Rewrite(spirvCross, ShaderStage.Pixel);

        result.Glsl.ShouldNotContain("float[](", Case.Sensitive);
        result.Glsl.ShouldContain("_42[3] = 10.0;", Case.Sensitive);
        GlslPortabilityAnalyzer.Analyze(result.Glsl, ShaderStage.Pixel, true, "t.fx", "PS")
            .ShouldNotContain(f => f.Code == "SD0403");
    }
}
