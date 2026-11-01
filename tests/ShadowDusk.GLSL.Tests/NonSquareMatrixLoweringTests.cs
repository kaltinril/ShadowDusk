#nullable enable

using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.GLSL;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Rule 17 (issue #453, the MonoGame 3.8.6 DesktopGL4 gap): every non-square matrix in the
/// versionless GLSL is held as a zero-padded square <c>matN</c>, because GLSL 1.10 / ESSL 1.00 has
/// no <c>mat2x3</c>…<c>mat4x3</c> and no matrix-from-matrix constructor. Strict front ends (Mesa,
/// WebGL) refused the old output at Effect load and DesktopGL4 aborted the process.
/// </summary>
public sealed class NonSquareMatrixLoweringTests
{
    private static string Lower(string body) => NonSquareMatrixLowering.Lower(body);

    private static string Shader(string statements, string globals = "") =>
        $"{globals}attribute vec4 vs_v0;\nuniform vec4 u[8];\n\nvoid main()\n{{\n{statements}\n}}\n";

    [Fact]
    public void ShaderWithoutANonSquareMatrix_IsReturnedUnchanged()
    {
        // Byte-identity for every shader that never warned SD0403: the pass does not even parse it.
        string body = Shader("    vec4 _1 = mat4(u[0], u[1], u[2], u[3]) * vs_v0;\n    gl_Position = _1;");
        Lower(body).ShouldBeSameAs(body);
    }

    [Fact]
    public void TruncationLocal_FromColumns_IsPaddedWithAZeroColumn()
    {
        // KNI DeferredBasicEffect's shape: DXC's (float3x3)World / mul(float3, float4x4) local.
        string body = Shader("    mat3x4 _60 = mat3x4(u[0], u[1], u[2]);\n    vec3 _7 = vec3((_60 * vs_v0.xyz).xyz);");

        string lowered = Lower(body);

        lowered.ShouldContain("mat4 _60 = mat4(u[0], u[1], u[2], vec4(0.0));", Case.Sensitive);
        // mat3x4 * vec3: the vector is padded to vec4 (the zero column meets a zero component).
        lowered.ShouldContain("vec3 _7 = vec3((_60 * vec4(vs_v0.xyz, 0.0)).xyz);", Case.Sensitive);
        lowered.ShouldNotContain("mat3x4", Case.Sensitive);
    }

    [Fact]
    public void MatrixTimesVector_NarrowsAPaddedResultToItsLogicalWidth()
    {
        // mat4x3 * vec4 -> vec3: the padded product is a vec4 whose w is zero.
        string body = Shader("    mat4x3 _9 = mat4x3(vs_v0.xyz, vs_v0.yzx, vs_v0.zxy, vs_v0.xxx);\n    vec3 _10 = _9 * vs_v0;");

        string lowered = Lower(body);

        lowered.ShouldContain(
            "mat4 _9 = mat4(vec4(vs_v0.xyz, 0.0), vec4(vs_v0.yzx, 0.0), vec4(vs_v0.zxy, 0.0), vec4(vs_v0.xxx, 0.0));",
            Case.Sensitive);
        lowered.ShouldContain("vec3 _10 = (_9 * vs_v0).xyz;", Case.Sensitive);
    }

    [Fact]
    public void VectorTimesMatrix_PadsTheVectorAndNarrowsTheResult()
    {
        string body = Shader(
            "    mat3x4 _2 = mat3x4(u[0], u[1], u[2]);\n" +
            "    mat4x3 _3 = mat4x3(u[0].xyz, u[1].xyz, u[2].xyz, u[3].xyz);\n" +
            "    vec3 _4 = vs_v0 * _2;\n" +
            "    vec4 _5 = vs_v0.xyz * _3;");

        string lowered = Lower(body);

        lowered.ShouldContain("vec3 _4 = (vs_v0 * _2).xyz;", Case.Sensitive);
        lowered.ShouldContain("vec4 _5 = vec4(vs_v0.xyz, 0.0) * _3;", Case.Sensitive);
    }

    [Fact]
    public void ColumnRead_IsNarrowed_ButAComponentReadIsLeftAlone()
    {
        string body = Shader(
            "    mat4x3 _2 = mat4x3(u[0].xyz, u[1].xyz, u[2].xyz, u[3].xyz);\n" +
            "    vec3 _3 = _2[1];\n" +
            "    float _4 = _2[2][1];\n" +
            "    float _5 = _2[3].z;\n" +
            "    _2[0] = _3;");

        string lowered = Lower(body);

        lowered.ShouldContain("vec3 _3 = _2[1].xyz;", Case.Sensitive);
        lowered.ShouldContain("float _4 = _2[2][1];", Case.Sensitive);
        lowered.ShouldContain("float _5 = _2[3].z;", Case.Sensitive);
        // A column write keeps the padding row zero: only the logical components are written.
        lowered.ShouldContain("_2[0].xyz = _3;", Case.Sensitive);
    }

    [Fact]
    public void MatrixFromMatrix_TakesTheUpperLeftAndPads()
    {
        string body = Shader(
            "    mat4 _1 = mat4(u[0], u[1], u[2], u[3]);\n" +
            "    mat3 _2 = mat3(u[0].xyz, u[1].xyz, u[2].xyz);\n" +
            "    mat3x4 _3 = mat3x4(_1);\n" +
            "    mat4x3 _4 = mat4x3(_2);\n" +
            "    mat3 _5 = mat3(_1);\n" +
            "    vec4 _6 = _3 * _5[0] + vec4(_4 * vs_v0, 1.0);");

        string lowered = Lower(body);

        lowered.ShouldContain("mat4 _3 = mat4(_1[0], _1[1], _1[2], vec4(0.0));", Case.Sensitive);
        // Columns past the source take GLSL's identity fill: column 3 of a 3-row matrix is all zero.
        lowered.ShouldContain(
            "mat4 _4 = mat4(vec4(_2[0], 0.0), vec4(_2[1], 0.0), vec4(_2[2], 0.0), vec4(0.0, 0.0, 0.0, 0.0));",
            Case.Sensitive);
        // A square matrix-from-matrix constructor is GLSL 1.20 too.
        lowered.ShouldContain("mat3 _5 = mat3(_1[0].xyz, _1[1].xyz, _1[2].xyz);", Case.Sensitive);
        lowered.ShouldContain("vec4 _6 = _3 * vec4(_5[0], 0.0) + vec4((_4 * vs_v0).xyz, 1.0);", Case.Sensitive);
    }

    [Fact]
    public void MatrixFromALargerMatrix_FillsMissingRowsFromTheIdentity()
    {
        // mat2x4(mat3): column c takes the mat3's column, row 3 from the identity (0 off the diagonal).
        string body = Shader(
            "    mat3 _2 = mat3(u[0].xyz, u[1].xyz, u[2].xyz);\n" +
            "    mat2x4 _3 = mat2x4(_2);\n" +
            "    vec4 _4 = _3 * vs_v0.xy;");

        Lower(body).ShouldContain(
            "mat4 _3 = mat4(vec4(_2[0], 0.0), vec4(_2[1], 0.0), vec4(0.0), vec4(0.0));", Case.Sensitive);
    }

    [Fact]
    public void ScalarListAndDiagonalConstructors_ArePadded()
    {
        string body = Shader(
            "    mat2x3 _1 = mat2x3(1.0, 2.0, 3.0, 4.0, 5.0, 6.0);\n" +
            "    mat3x2 _2 = mat3x2(0.5);\n" +
            "    gl_Position = vec4(_1 * vs_v0.xy, (_2 * vs_v0.xyz).x);");

        string lowered = Lower(body);

        lowered.ShouldContain("mat3 _1 = mat3(vec3(1.0, 2.0, 3.0), vec3(4.0, 5.0, 6.0), vec3(0.0));", Case.Sensitive);
        // The diagonal stops at the logical rows: element (2,2) is padding, so it stays zero.
        lowered.ShouldContain("mat3 _2 = mat3(vec3(0.5, 0.0, 0.0), vec3(0.0, 0.5, 0.0), vec3(0.0, 0.0, 0.0));", Case.Sensitive);
        lowered.ShouldContain("gl_Position = vec4(_1 * vec3(vs_v0.xy, 0.0), (_2 * vs_v0.xyz).x);", Case.Sensitive);
    }

    [Fact]
    public void ProductOfTwoPaddedMatrices_StaysPadded()
    {
        // mat3x4 * mat4x3 = mat4; mat2x3 * mat3x2 = mat3. Both factors are held at the result's size.
        string body = Shader(
            "    mat3x4 _1 = mat3x4(u[0], u[1], u[2]);\n" +
            "    mat4x3 _2 = mat4x3(u[0].xyz, u[1].xyz, u[2].xyz, u[3].xyz);\n" +
            "    mat4 _3 = _1 * _2;\n" +
            "    gl_Position = _3 * vs_v0;");

        string lowered = Lower(body);

        lowered.ShouldContain("mat4 _3 = _1 * _2;", Case.Sensitive);
        lowered.ShouldNotContain("mat3x4", Case.Sensitive);
        lowered.ShouldNotContain("mat4x3", Case.Sensitive);
    }

    [Fact]
    public void ProductHeldAtALargerSize_IsWidenedThenNarrowed()
    {
        // mat4x3 * mat3x4 = mat3, computed at 4 and narrowed; mat3x2 * mat2x3 = mat2, likewise at 3;
        // mat2x3 * mat4x2 = mat4x3: the mat2x3 (held at 3) is widened to 4 first.
        string body = Shader(
            "    mat4x3 _1 = mat4x3(u[0].xyz, u[1].xyz, u[2].xyz, u[3].xyz);\n" +
            "    mat3x4 _2 = mat3x4(u[0], u[1], u[2]);\n" +
            "    mat3 _3 = _1 * _2;\n" +
            "    mat3x2 _4 = mat3x2(u[0].xy, u[1].xy, u[2].xy);\n" +
            "    mat2x3 _5 = mat2x3(u[0].xyz, u[1].xyz);\n" +
            "    mat2 _6 = _4 * _5;\n" +
            "    mat4x2 _7 = mat4x2(u[0].xy, u[1].xy, u[2].xy, u[3].xy);\n" +
            "    mat4x3 _8 = _5 * _7;\n" +
            "    gl_Position = vec4(_3 * _8 * vs_v0, 1.0) + vec4(_6[0], 0.0, 0.0);");

        string lowered = Lower(body);

        lowered.ShouldContain("mat3 _3 = mat3((_1 * _2)[0].xyz, (_1 * _2)[1].xyz, (_1 * _2)[2].xyz);", Case.Sensitive);
        lowered.ShouldContain("mat2 _6 = mat2((_4 * _5)[0].xy, (_4 * _5)[1].xy);", Case.Sensitive);
        lowered.ShouldContain(
            "mat4 _8 = mat4(vec4(_5[0], 0.0), vec4(_5[1], 0.0), vec4(_5[2], 0.0), vec4(0.0)) * _7;", Case.Sensitive);
        // mat3 * mat4x3: the square mat3 is widened to the mat4x3's size.
        lowered.ShouldContain(
            "gl_Position = vec4((mat4(vec4(_3[0], 0.0), vec4(_3[1], 0.0), vec4(_3[2], 0.0), vec4(0.0)) * _8 * vs_v0).xyz, 1.0)",
            Case.Sensitive);
        lowered.ShouldNotMatch(@"\bmat[234]x[234]\b");
    }

    [Fact]
    public void ScalarScaleNegateAndSameTypeSum_PassThrough()
    {
        string body = Shader(
            "    mat3x4 _1 = mat3x4(u[0], u[1], u[2]);\n" +
            "    mat3x4 _2 = (-_1 + _1 * 2.0) / 3.0;\n" +
            "    gl_Position = _2 * vs_v0.xyz;");

        string lowered = Lower(body);

        lowered.ShouldContain("mat4 _2 = (-_1 + _1 * 2.0) / 3.0;", Case.Sensitive);
        lowered.ShouldContain("gl_Position = _2 * vec4(vs_v0.xyz, 0.0);", Case.Sensitive);
    }

    [Theory]
    // A scalar added to a matrix would write the scalar into the padding.
    [InlineData("    mat3x4 _1 = mat3x4(u[0], u[1], u[2]);\n    mat3x4 _2 = _1 + 1.0;\n    gl_Position = _2 * vs_v0.xyz;")]
    // A matrix operand with a side effect cannot be repeated once per column.
    [InlineData("    mat3x2 _1 = mat3x2(u[0].xy, u[1].xy, u[2].xy);\n    mat2x3 _2 = mat2x3(u[0].xyz, u[1].xyz);\n    mat2 _3 = _1 * (_2 = _2);\n    gl_Position = vec4(_3[0], 0.0, 1.0);")]
    // An operand whose type the pass cannot see.
    [InlineData("    mat3x4 _1 = mat3x4(u[0], u[1], u[2]);\n    gl_Position = _1 * someUnknown;")]
    // A vector built from a matrix's flattened components would read the padding.
    [InlineData("    mat2x3 _1 = mat2x3(u[0].xyz, u[1].xyz);\n    gl_Position = vec4(_1);")]
    public void UnmodelledShape_DeclinesTheWholePass_SoSd0403StillWarns(string statements)
    {
        // All-or-nothing: a partial lowering would leave a matN declaration beside an unlowered use.
        string body = Shader(statements);

        Lower(body).ShouldBe(body);
    }

    [Fact]
    public void NonSquareStructMemberOrFunctionParameter_DeclinesThePass()
    {
        string inStruct = "struct S\n{\n    mat3x4 m;\n};\n\nvoid main()\n{\n    S s;\n    gl_Position = s.m * vec3(1.0);\n}\n";
        string inParam = "vec4 f(mat3x4 m)\n{\n    return m * vec3(1.0);\n}\n\nvoid main()\n{\n    gl_Position = f(mat3x4(vec4(1.0), vec4(1.0), vec4(1.0)));\n}\n";

        Lower(inStruct).ShouldBe(inStruct);
        Lower(inParam).ShouldBe(inParam);
    }

    [Fact]
    public void NonSquareInterfaceDeclaration_IsNeverRetyped()
    {
        // A non-square uniform changes what the runtime binds (refused upstream as SD0210); never re-typed.
        string body = "uniform mat3x4 m;\n\nvoid main()\n{\n    gl_Position = m * vec3(1.0);\n}\n";

        Lower(body).ShouldBe(body);
    }

    // ---- Through the rewriter: SPIRV-Cross's truncation local, and Rule 4c's own interpolant temps ----

    private static MonoGameGlslInterpolantPacking Packing(string semantic, bool rowMajor = false) =>
        new(rowMajor ? new HashSet<string>(StringComparer.Ordinal) { semantic } : new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal))
        {
            Declared = new HashSet<string>(StringComparer.Ordinal) { semantic },
        };

    [Theory]
    [InlineData(false, "vTexCoord1.xyzw = vec4(vs_oT1_mat[0][0], vs_oT1_mat[1][0], vs_oT1_mat[2][0], vs_oT1_mat[3][0]);")]
    [InlineData(true, "vTexCoord1.xyz = vs_oT1_mat[0].xyz;")]
    public void NonSquareInterpolant_VertexTemp_IsHeldPadded(bool rowMajor, string copyOut)
    {
        // HLSL float4x3 -> GLSL mat4x3 (4 columns of vec3).
        const string vs = """
#version 140
in vec4 in_var_POSITION0;
in vec3 in_var_NORMAL0;
out mat4x3 out_var_TEXCOORD1;
void main()
{
    gl_Position = in_var_POSITION0;
    out_var_TEXCOORD1 = mat4x3(in_var_NORMAL0, in_var_NORMAL0.yzx, in_var_NORMAL0.zxy, in_var_POSITION0.xyz);
    gl_Position.z = 2.0 * gl_Position.z - gl_Position.w;
}
""";
        string glsl = MonoGameGlslRewriter.Rewrite(vs, ShaderStage.Vertex, null, Packing("TEXCOORD1", rowMajor)).Glsl;

        glsl.ShouldContain("mat4 vs_oT1_mat;", Case.Sensitive);
        glsl.ShouldContain(
            "vs_oT1_mat = mat4(vec4(vs_v1.xyz, 0.0), vec4(vs_v1.yzx, 0.0), vec4(vs_v1.zxy, 0.0), vec4(vs_v0.xyz, 0.0));",
            Case.Sensitive);
        glsl.ShouldContain(copyOut, Case.Sensitive);
        glsl.ShouldNotMatch(@"\bmat[234]x[234]\b");
        GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Vertex, true, "t.fx", "VS").ShouldNotContain(e => e.Code == "SD0403");
    }

    [Fact]
    public void NonSquareInterpolant_PixelRebuild_IsPadded()
    {
        const string ps = """
#version 140
in mat4x3 in_var_TEXCOORD1;
out vec4 out_var_SV_Target;
void main()
{
    out_var_SV_Target = vec4(in_var_TEXCOORD1 * vec4(0.5, 0.25, 0.125, 0.0625), in_var_TEXCOORD1[3].x);
}
""";
        string glsl = MonoGameGlslRewriter.Rewrite(ps, ShaderStage.Pixel, null, Packing("TEXCOORD1")).Glsl;

        const string rebuilt = "mat4(vec4(vec3(vTexCoord1.x, vTexCoord2.x, vTexCoord3.x), 0.0), " +
                               "vec4(vec3(vTexCoord1.y, vTexCoord2.y, vTexCoord3.y), 0.0), " +
                               "vec4(vec3(vTexCoord1.z, vTexCoord2.z, vTexCoord3.z), 0.0), " +
                               "vec4(vec3(vTexCoord1.w, vTexCoord2.w, vTexCoord3.w), 0.0))";
        glsl.ShouldContain($"vec4(({rebuilt} * vec4(0.5, 0.25, 0.125, 0.0625)).xyz, {rebuilt}[3].x)", Case.Sensitive);
        glsl.ShouldNotMatch(@"\bmat[234]x[234]\b");
        GlslPortabilityAnalyzer.Analyze(glsl, ShaderStage.Pixel, true, "t.fx", "PS").ShouldNotContain(e => e.Code == "SD0403");
    }
}
