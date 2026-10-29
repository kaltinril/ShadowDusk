#nullable enable

using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests;

/// <summary>
/// The global-initializer reader: the evaluator that turns <c>float R = 1;</c> into the default
/// blob an <c>.mgfx</c> carries (DXC and vkd3d drop the initializer, so the source is the only
/// place it survives). Pure tests over hand-written preprocessed text, plus a few through the
/// real preprocessor entry point (<see cref="GlobalInitializerReader.Read"/>).
/// </summary>
public sealed class GlobalInitializerReaderTests
{
    private static GlobalInitializers Read(string hlsl)
    {
        GlobalInitializers result = GlobalInitializerReader.ReadPreprocessed(hlsl, "t.fx");
        return result;
    }

    private static float[] Floats(byte[] blob)
    {
        var f = new float[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, f, 0, blob.Length);
        return f;
    }

    private static int[] Ints(byte[] blob)
    {
        var i = new int[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, i, 0, blob.Length);
        return i;
    }

    private static GlobalInitializerDefault One(string hlsl, string name)
    {
        GlobalInitializers r = Read(hlsl);
        r.Failures.ShouldBeEmpty(string.Join(" | ", r.Failures.Select(f => $"{f.Name}: {f.Reason}")));
        r.Defaults.ContainsKey(name).ShouldBeTrue();
        return r.Defaults[name];
    }

    // ---- the minimal case, byte for byte -------------------------------------------------

    [Fact]
    public void FloatRIsOne_IsTheFourBytes_00_00_80_3F()
    {
        GlobalInitializerDefault d = One("float R = 1;", "R");

        d.Class.ShouldBe((byte)0);
        d.Type.ShouldBe((byte)3);
        d.Rows.ShouldBe(1);
        d.Columns.ShouldBe(1);
        d.ArrayLength.ShouldBe(0);
        d.ElementBlobs.Count.ShouldBe(1);
        d.ElementBlobs[0].ShouldBe(new byte[] { 0x00, 0x00, 0x80, 0x3F });
    }

    [Fact]
    public void NoInitializer_YieldsNoDefaultAndNoFailure()
    {
        GlobalInitializers r = Read("float4 Tint;\nfloat X;");
        r.Defaults.ShouldBeEmpty();
        r.Failures.ShouldBeEmpty();
    }

    // ---- shapes ---------------------------------------------------------------------------

    [Fact]
    public void Vector_Constructor()
    {
        GlobalInitializerDefault d = One("float4 Tint = float4(1, 0, 0, 1);", "Tint");
        d.Class.ShouldBe((byte)1);
        d.Rows.ShouldBe(1);
        d.Columns.ShouldBe(4);
        Floats(d.ElementBlobs[0]).ShouldBe(new[] { 1f, 0f, 0f, 1f });
    }

    [Fact]
    public void Vector_BraceList_And_Splat_And_Cast()
    {
        Floats(One("float3 C = {1, 2, 3};", "C").ElementBlobs[0]).ShouldBe(new[] { 1f, 2f, 3f });
        Floats(One("float4 C = 0.5;", "C").ElementBlobs[0]).ShouldBe(new[] { 0.5f, 0.5f, 0.5f, 0.5f });
        Floats(One("float2 C = (float2)0.25;", "C").ElementBlobs[0]).ShouldBe(new[] { 0.25f, 0.25f });
        Floats(One("float4 C = float4(0.5);", "C").ElementBlobs[0]).ShouldBe(new[] { 0.5f, 0.5f, 0.5f, 0.5f });
    }

    [Fact]
    public void Vector_ConstructorFromMixedParts_AndSwizzles()
    {
        const string src = """
            static const float2 UV = float2(0.25, 0.75);
            float4 C = float4(UV.yx, 0, 1);
            """;
        Floats(One(src, "C").ElementBlobs[0]).ShouldBe(new[] { 0.75f, 0.25f, 0f, 1f });
    }

    [Fact]
    public void UnaryMinus_And_Literals()
    {
        Floats(One("float2 V = float2(-0.5, +2.0f);", "V").ElementBlobs[0]).ShouldBe(new[] { -0.5f, 2f });
        Floats(One("float X = .5;", "X").ElementBlobs[0]).ShouldBe(new[] { 0.5f });
        Floats(One("float X = 1e-2;", "X").ElementBlobs[0]).ShouldBe(new[] { 0.01f });
        Floats(One("float X = 2.h;", "X").ElementBlobs[0]).ShouldBe(new[] { 2f });
    }

    [Fact]
    public void ConstantFolding_FollowsHlslIntegerAndFloatRules()
    {
        // Float arithmetic rounds to 32 bits at each step.
        Floats(One("float X = 1.0 / 4.0 + 0.25;", "X").ElementBlobs[0]).ShouldBe(new[] { 0.5f });
        Floats(One("float X = (1.0 + 2.0) * 0.5;", "X").ElementBlobs[0]).ShouldBe(new[] { 1.5f });
        // Integer literals divide as integers (HLSL), then convert to the declared type.
        Floats(One("float X = 1 / 2;", "X").ElementBlobs[0]).ShouldBe(new[] { 0f });
        Floats(One("float X = 3.0 / 2;", "X").ElementBlobs[0]).ShouldBe(new[] { 1.5f });
        Floats(One("float X = true ? 4.0 : 8.0;", "X").ElementBlobs[0]).ShouldBe(new[] { 4f });
        Floats(One("float X = max(1.0, 2.5) + saturate(7);", "X").ElementBlobs[0]).ShouldBe(new[] { 3.5f });
        Floats(One("float X = radians(180.0);", "X").ElementBlobs[0]).ShouldBe(new[] { MathF.PI });
    }

    [Fact]
    public void EarlierConsts_AreUsable_ButNonConstGlobalsAreNot()
    {
        const string src = """
            static const float TAU = 6.28125;
            const float HALF_TAU = TAU * 0.5;
            float Phase = HALF_TAU;
            """;
        Floats(One(src, "Phase").ElementBlobs[0]).ShouldBe(new[] { 3.140625f });

        GlobalInitializers r = Read("float A = 1;\nfloat B = A;");
        r.Defaults.ContainsKey("A").ShouldBeTrue();
        r.Defaults.ContainsKey("B").ShouldBeFalse();
        r.Failures.Single().Name.ShouldBe("B");
        r.Failures.Single().Reason.ShouldContain("'A'", Case.Sensitive);
    }

    [Fact]
    public void Int_And_Bool_AreRawInts_AndTrueIsAllOnes()
    {
        GlobalInitializerDefault mode = One("int Mode = 2;", "Mode");
        mode.Type.ShouldBe((byte)2);
        Ints(mode.ElementBlobs[0]).ShouldBe(new[] { 2 });

        GlobalInitializerDefault flag = One("bool Flag = true;", "Flag");
        flag.Type.ShouldBe((byte)1);
        // D3D reflection hands mgfxc TRUE = 0xFFFFFFFF; the blob matches it.
        Ints(flag.ElementBlobs[0]).ShouldBe(new[] { -1 });

        Ints(One("bool Off = false;", "Off").ElementBlobs[0]).ShouldBe(new[] { 0 });
        Ints(One("bool B = 3 > 2;", "B").ElementBlobs[0]).ShouldBe(new[] { -1 });
        Ints(One("int M = 0x10 | 1;", "M").ElementBlobs[0]).ShouldBe(new[] { 17 });
        Ints(One("int N = (int)2.9;", "N").ElementBlobs[0]).ShouldBe(new[] { 2 });
        Ints(One("int3 V = int3(1, -2, 3);", "V").ElementBlobs[0]).ShouldBe(new[] { 1, -2, 3 });
        Ints(One("uint U = 7u;", "U").ElementBlobs[0]).ShouldBe(new[] { 7 });
    }

    [Fact]
    public void Matrix_IsWrittenColumnByColumn_LikeEffectParameterSetValue()
    {
        // HLSL fills a constructor row-major: M[0] = (1,2,3,4) ... M[3] = (13,14,15,16).
        // MonoGame's EffectParameter.SetValue(Matrix) leaves Data[c * 4 + r] = M[r][c]
        // ("HLSL expects matrices to be transposed by default").
        GlobalInitializerDefault d = One(
            "float4x4 W = float4x4(1,2,3,4, 5,6,7,8, 9,10,11,12, 13,14,15,16);", "W");
        d.Class.ShouldBe((byte)2);
        d.Rows.ShouldBe(4);
        d.Columns.ShouldBe(4);
        Floats(d.ElementBlobs[0]).ShouldBe(new float[] { 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 4, 8, 12, 16 });
    }

    [Fact]
    public void Matrix_NonSquare_KeepsRowsAndColumns_AndAcceptsNestedRowsAndSplat()
    {
        GlobalInitializerDefault d = One("float2x3 M = {1,2,3, 4,5,6};", "M");
        d.Rows.ShouldBe(2);
        d.Columns.ShouldBe(3);
        // Data[c * rows + r] = M[r][c], rows = 2, cols = 3
        Floats(d.ElementBlobs[0]).ShouldBe(new float[] { 1, 4, 2, 5, 3, 6 });

        Floats(One("float2x2 M = {{1,2},{3,4}};", "M").ElementBlobs[0]).ShouldBe(new float[] { 1, 3, 2, 4 });
        Floats(One("float2x2 M = float2x2(0.5, 0, 0, 0.5);", "M").ElementBlobs[0]).ShouldBe(new float[] { 0.5f, 0, 0, 0.5f });
        Floats(One("float2x2 M = 0.5;", "M").ElementBlobs[0]).ShouldBe(new float[] { 0.5f, 0.5f, 0.5f, 0.5f });
    }

    [Fact]
    public void Array_EachElementCarriesItsOwnBlob()
    {
        GlobalInitializerDefault d = One(
            "float4 Colors[2] = { float4(1, 0, 0, 1), float4(0, 1, 0, 1) };", "Colors");
        d.ArrayLength.ShouldBe(2);
        d.ElementBlobs.Count.ShouldBe(2);
        Floats(d.ElementBlobs[0]).ShouldBe(new[] { 1f, 0f, 0f, 1f });
        Floats(d.ElementBlobs[1]).ShouldBe(new[] { 0f, 1f, 0f, 1f });
    }

    [Fact]
    public void Array_Scalars_FlatVectorsAndNestedBraces()
    {
        GlobalInitializerDefault w = One("float W[3] = {0.25, 0.5, 0.75};", "W");
        w.ElementBlobs.Select(b => Floats(b)[0]).ShouldBe(new[] { 0.25f, 0.5f, 0.75f });

        // The components laid flat, as HLSL allows.
        GlobalInitializerDefault flat = One("float2 P[2] = {1,2, 3,4};", "P");
        Floats(flat.ElementBlobs[0]).ShouldBe(new[] { 1f, 2f });
        Floats(flat.ElementBlobs[1]).ShouldBe(new[] { 3f, 4f });

        GlobalInitializerDefault nested = One("float2 P[2] = { {1,2}, {3,4} };", "P");
        Floats(nested.ElementBlobs[1]).ShouldBe(new[] { 3f, 4f });
    }

    [Fact]
    public void Array_LengthMayBeAnEarlierConst()
    {
        const string src = "static const int N = 2;\nfloat W[N] = {1, 2};";
        One(src, "W").ArrayLength.ShouldBe(2);
    }

    // ---- scope: what is, and is not, a uniform default ------------------------------------

    [Fact]
    public void Statics_Locals_Structs_Textures_And_Functions_AreNotDefaults()
    {
        const string src = """
            static float S = 5;
            Texture2D Tex;
            SamplerState Samp;
            struct Light { float3 Dir; float Power; };
            float4 PS(float2 uv : TEXCOORD0) : SV_Target
            {
                float local = 7;
                return float4(local, S, 0, 1);
            }
            float Real = 3;
            """;
        GlobalInitializers r = Read(src);
        r.Defaults.Keys.ShouldBe(new[] { "Real" });
        r.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void CbufferMembers_AreScanned_AndScanningContinuesAfterTheBlock()
    {
        const string src = """
            cbuffer Params : register(b0)
            {
                float A = 1;
                float4 B = float4(0, 1, 0, 1);
            };
            float C = 3;
            """;
        GlobalInitializers r = Read(src);
        r.Defaults.Keys.OrderBy(k => k).ShouldBe(new[] { "A", "B", "C" });
    }

    [Fact]
    public void Semantics_Registers_Annotations_And_MultipleDeclarators()
    {
        const string src = """
            uniform float A : register(c0) = 1, B = 2;
            float4 C <string UIName = "c"; > = float4(1,2,3,4);
            extern const float D = 4;
            """;
        GlobalInitializers r = Read(src);
        r.Failures.ShouldBeEmpty();
        Floats(r.Defaults["A"].ElementBlobs[0]).ShouldBe(new[] { 1f });
        Floats(r.Defaults["B"].ElementBlobs[0]).ShouldBe(new[] { 2f });
        Floats(r.Defaults["C"].ElementBlobs[0]).ShouldBe(new[] { 1f, 2f, 3f, 4f });
        Floats(r.Defaults["D"].ElementBlobs[0]).ShouldBe(new[] { 4f });
    }

    [Fact]
    public void TemplateTypeSpellings()
    {
        Floats(One("vector<float, 3> V = float3(1,2,3);", "V").ElementBlobs[0]).ShouldBe(new[] { 1f, 2f, 3f });
        GlobalInitializerDefault m = One("matrix<float, 2, 2> M = float2x2(1,2,3,4);", "M");
        m.Rows.ShouldBe(2);
        Floats(m.ElementBlobs[0]).ShouldBe(new float[] { 1, 3, 2, 4 });
    }

    [Fact]
    public void Half_And_Float1_ReflectAsFloatScalars()
    {
        GlobalInitializerDefault h = One("half H = 0.5;", "H");
        h.Type.ShouldBe((byte)3);
        h.Class.ShouldBe((byte)0);
        One("float1 F = 2;", "F").Class.ShouldBe((byte)0);
    }

    // ---- fail loudly ----------------------------------------------------------------------

    [Fact]
    public void AnUnmodelledCall_IsAFailureNamingTheVariable_NeverAZeroDefault()
    {
        GlobalInitializers r = Read("float4 Tint = Lerp3(1);\nfloat Fine = 1;");
        r.Defaults.Keys.ShouldBe(new[] { "Fine" });
        GlobalInitializerFailure f = r.Failures.Single();
        f.Name.ShouldBe("Tint");
        f.Reason.ShouldContain("Lerp3", Case.Sensitive);
    }

    [Theory]
    [InlineData("float3 V = float4(1,2,3,4);", "4")]               // wrong component count
    [InlineData("float4 V = float4(1,2,3);", "3 component")]       // too few constructor args
    [InlineData("float W[2] = {1};", "2 element")]                 // too few array items
    [InlineData("float W[2] = 1;", "brace list")]                  // array needs braces
    [InlineData("float X = 1 / 0;", "division by zero")]
    [InlineData("float X = 1 +;", "")]
    [InlineData("float X = (1;", "")]
    public void MalformedOrMismatched_IsAFailure(string src, string reasonFragment)
    {
        GlobalInitializers r = Read(src);
        r.Defaults.ShouldBeEmpty();
        r.Failures.Count.ShouldBe(1);
        if (reasonFragment.Length > 0)
            r.Failures[0].Reason.ShouldContain(reasonFragment, Case.Sensitive);
    }

    [Fact]
    public void AFailureIsNotRecordedForAStatic()
    {
        GlobalInitializers r = Read("static float4 Cache = NotAFunction(1);\nfloat Fine = 1;");
        r.Failures.ShouldBeEmpty();
        r.Defaults.Keys.ShouldBe(new[] { "Fine" });
    }

    [Fact]
    public void Int_Division_ByZero_And_Bad_Matrix_Splat_Shapes_Fail()
    {
        Read("float2x2 M = float4(1,2,3,4);").Failures.Count.ShouldBe(1);
    }

    // ---- positions ------------------------------------------------------------------------

    [Fact]
    public void ReportsTheLineOfTheDeclaredName_ThroughLineDirectives()
    {
        const string src = """
            #line 10 "lib.fxh"
            float A = 1;

            #line 4 "main.fx"
            float4 Tint = float4(1, 0, 0, 1);
            float Bad = Nope();
            """;
        GlobalInitializers r = Read(src);
        r.Defaults["A"].File.ShouldBe("lib.fxh");
        r.Defaults["A"].Line.ShouldBe(10);
        r.Defaults["Tint"].File.ShouldBe("main.fx");
        r.Defaults["Tint"].Line.ShouldBe(4);
        r.Defaults["Tint"].Column.ShouldBe(8);
        r.Failures.Single().Line.ShouldBe(5);
    }

    // ---- review findings (adversarial review of the first cut) -----------------------------

    [Fact]
    public void DeepNesting_IsAFailure_NotAStackOverflow()
    {
        string parens = "float A = " + new string('(', 400) + "1" + new string(')', 400) + ";";
        Read(parens).Failures.Single().Reason.ShouldContain("nested too deeply", Case.Sensitive);

        // A long run of prefix operators is applied in a loop, so a moderate one still evaluates...
        Floats(One("float A = " + new string('-', 1000) + "1;", "A").ElementBlobs[0]).ShouldBe(new[] { 1f });
        // ...and an absurd one is refused rather than recursing.
        Read("float A = " + new string('!', 50_000) + "1;").Failures.Count.ShouldBe(1);
        Read("float A = " + string.Concat(Enumerable.Repeat("(float)", 20_000)) + "1;").Failures.Count.ShouldBe(1);
    }

    [Fact]
    public void HugeArray_IsAFailure_NotAnException()
    {
        Read("float4 A[1073741824] = {};").Failures.Single().Reason.ShouldContain("array length", Case.Sensitive);
        Read("static float4 A[1073741824] = {};").Failures.ShouldBeEmpty();   // a static is no uniform
    }

    [Fact]
    public void AGlobalAfterAFunctionReturningAnElaboratedStruct_IsStillRead()
    {
        const string src = """
            struct PSOut { float4 c : COLOR0; };
            struct PSOut Helper(float x) { struct PSOut o; o.c = x; return o; }
            float After = 7;
            """;
        Floats(One(src, "After").ElementBlobs[0]).ShouldBe(new[] { 7f });
    }

    [Fact]
    public void Unsigned_Semantics()
    {
        Floats(One("float B = 4294967295u;", "B").ElementBlobs[0]).ShouldBe(new[] { 4294967296f });
        BitConverter.ToUInt32(One("uint C = 4000000000u / 2u;", "C").ElementBlobs[0]).ShouldBe(2_000_000_000u);
        Ints(One("bool A = 3000000000u > 1u;", "A").ElementBlobs[0]).ShouldBe(new[] { -1 });
        Floats(One("float D = 0xFFFFFFFF;", "D").ElementBlobs[0]).ShouldBe(new[] { 4294967296f });
        BitConverter.ToUInt32(One("uint E = 0xFFFFFFFF;", "E").ElementBlobs[0]).ShouldBe(uint.MaxValue);
        Ints(One("int F = 0x7FFFFFFF;", "F").ElementBlobs[0]).ShouldBe(new[] { int.MaxValue });
        One("uint G = 1;", "G").Type.ShouldBe((byte)2);
    }

    [Fact]
    public void LeadingZeroIntegerIsOctal()
    {
        Ints(One("int A = 010;", "A").ElementBlobs[0]).ShouldBe(new[] { 8 });
        Floats(One("float A = 0.5;", "A").ElementBlobs[0]).ShouldBe(new[] { 0.5f });
        Read("int A = 09;").Failures.Count.ShouldBe(1);
    }

    [Fact]
    public void Ternary_ResultTakesTheCommonType()
    {
        Floats(One("float E = (true ? 1 : 2.5) / 2;", "E").ElementBlobs[0]).ShouldBe(new[] { 0.5f });
        Ints(One("int E = false ? 1 : 2;", "E").ElementBlobs[0]).ShouldBe(new[] { 2 });
    }

    [Fact]
    public void Typedefs_ConstAfterTheType_And_UnsignedInt_AreRead()
    {
        const string src = """
            typedef float4 col_t;
            col_t Tint = float4(1,0,0,1);
            float const Half = 0.5;
            unsigned int V = 3;
            """;
        GlobalInitializers r = Read(src);
        r.Failures.ShouldBeEmpty(string.Join("|", r.Failures.Select(f => f.Reason)));
        Floats(r.Defaults["Tint"].ElementBlobs[0]).ShouldBe(new[] { 1f, 0f, 0f, 1f });
        Floats(r.Defaults["Half"].ElementBlobs[0]).ShouldBe(new[] { 0.5f });
        Ints(r.Defaults["V"].ElementBlobs[0]).ShouldBe(new[] { 3 });
    }

    [Fact]
    public void AnInitializerOnAnUnmodelledType_IsAFailure_ButSamplerStatesAndStaticsAreNot()
    {
        const string src = """
            double D = 2;
            Texture2D Tex;
            SamplerState S = sampler_state { Filter = MIN_MAG_MIP_POINT; };
            sampler2D Old = sampler_state { Texture = <Tex>; };
            static Weird W = 1;
            """;
        GlobalInitializers r = Read(src);
        r.Failures.Select(f => f.Name).ShouldBe(new[] { "D" });
        r.Failures[0].Reason.ShouldContain("'double'", Case.Sensitive);
    }

    [Fact]
    public void ColumnsStayRight_OnLongLinesAndAfterBlockComments()
    {
        GlobalInitializerDefault d = One("/* a\n b */ float4 Tint = 1;", "Tint");
        d.Line.ShouldBe(2);
        d.Column.ShouldBe(14);
        string longLine = string.Concat(Enumerable.Repeat("float A_ = 1; ", 5000)) + "float Z = 2;";
        One(longLine, "Z").Column.ShouldBe(14 * 5000 + 7);
    }

    [Fact]
    public void Read_ReportsAConditionalOnACompilerPredefinedMacro()
    {
        const string src = """
            #if __HLSL_VERSION >= 2018
            float A1 = 1;
            #else
            float A1 = 2;
            #endif
            """;
        var result = GlobalInitializerReader.Read(src, "t.fx");
        result.IsSuccess.ShouldBeTrue();
        result.Value.CompilerPredefinedMacro.ShouldNotBeNull();
        result.Value.CompilerPredefinedMacro!.ShouldContain("__HLSL_VERSION", Case.Sensitive);

        GlobalInitializerReader.Read("float A = 1;", "t.fx").Value.CompilerPredefinedMacro.ShouldBeNull();
    }

    // ---- through the real preprocessor ----------------------------------------------------

    [Fact]
    public void Read_ExpandsMacros_AndHonoursConditionals()
    {
        const string src = """
            #define OPENGL 1
            #define TINT float4(1, 0, 0, 1)
            #define TWICE(x) ((x) * 2)
            #if OPENGL
            float4 Tint = TINT;
            float Gain = TWICE(0.25);
            #else
            float4 Tint = float4(0, 0, 1, 1);
            #endif
            """;
        var result = GlobalInitializerReader.Read(src, "t.fx");
        result.IsSuccess.ShouldBeTrue();
        GlobalInitializers r = result.Value;
        Floats(r.Defaults["Tint"].ElementBlobs[0]).ShouldBe(new[] { 1f, 0f, 0f, 1f });
        Floats(r.Defaults["Gain"].ElementBlobs[0]).ShouldBe(new[] { 0.5f });
    }

    [Fact]
    public void Read_AFxFileWithTechniquesAndAnnotations_StillFindsTheDefault()
    {
        const string src = """
            #define PS ps_4_0_level_9_1
            float R = 1;
            float4 main() : COLOR0 { return float4(R, 0, 0, 1); }
            technique T { pass P { PixelShader = compile PS main(); } }
            """;
        var result = GlobalInitializerReader.Read(src, "t.fx");
        result.IsSuccess.ShouldBeTrue();
        result.Value.Defaults.Keys.ShouldBe(new[] { "R" });
        result.Value.Failures.ShouldBeEmpty();
    }
}
