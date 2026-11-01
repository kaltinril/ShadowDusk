#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Compiler.Tests.Sksl;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Issue #435: the SkSL and raylib conversion results report each uniform's declared HLSL default
/// (<see cref="SkslUniform.DefaultValue"/>, <see cref="RaylibUniform.DefaultValue"/>), with the
/// SAME values and layout as the MGFX route's <see cref="ParameterReflection.DefaultValue"/> for the
/// same source, while the converted shader text stays exactly what it was (the uniform is still
/// declared without a value; issue #424's text option is not taken). The MGFX baseline is the
/// OpenGL target where it models the type, otherwise DirectX 11 (OpenGL refuses int/bool and
/// non-4x4 matrices with SD0210). Needs the DXC, SPIRV-Cross and vkd3d natives, like the
/// converters' other tests; the Skia renders are real SkiaSharp (CPU raster).
/// </summary>
public sealed class ConverterUniformDefaultsTests
{
    private const string Header = "#if OPENGL\n#define PS ps_3_0\n#else\n#define PS ps_4_0\n#endif\n";

    private static string Fx(string declarations, string use) =>
        Header + declarations + "\n" +
        "float4 main(float2 uv : TEXCOORD0) : COLOR0 { return float4(" + use + ", 0, 0, 1); }\n" +
        "technique T { pass P { PixelShader = compile PS main(); } }\n";

    /// <summary>One initializer shape: the source, the uniform it pins, and where it can be compared.</summary>
    private sealed record Shape(string Declarations, string Use, string Uniform, float[] Expected, bool OnGl, bool OnRaylib);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        ["scalar"] = new("float Thickness = 1.0;", "Thickness", "Thickness", [1f], OnGl: true, OnRaylib: true),
        ["vector"] = new("float4 Col = float4(0.1, 0.2, 0.3, 0.4);", "Col.x + Col.w", "Col", [0.1f, 0.2f, 0.3f, 0.4f], true, true),
        ["splat"] = new("float3 S = 2;", "S.y", "S", [2f, 2f, 2f], true, true),
        ["folded-const"] = new("static const float K = 0.5;\nfloat Gain = K * 2 + 0.25;", "Gain", "Gain", [1.25f], true, true),
        ["macro"] = new("#define TINT float4(0.25, 0.5, 0.75, 1)\nfloat4 Tint = TINT;", "Tint.y", "Tint", [0.25f, 0.5f, 0.75f, 1f], true, true),
        ["cbuffer"] = new("cbuffer Params { float A = 2; float4 C = 1; };", "A + C.x", "C", [1f, 1f, 1f, 1f], true, true),
        ["float-array"] = new("float Arr[3] = { 0.5, 0.25, 0.125 };", "Arr[0] + Arr[2]", "Arr", [0.5f, 0.25f, 0.125f], true, true),
        ["vec2-array"] = new("float2 VArr[2] = { float2(1, 2), float2(3, 4) };", "VArr[1].x", "VArr", [1f, 2f, 3f, 4f], true, true),
        ["vec4-array"] = new("float4 Colors[2] = { float4(1, 0, 0, 1), float4(0, 1, 0, 1) };", "Colors[0].x + Colors[1].y", "Colors", [1f, 0f, 0f, 1f, 0f, 1f, 0f, 1f], true, true),
        ["float4x4"] = new("float4x4 M = float4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16);", "mul(float4(uv, 0, 1), M).x", "M",
            [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f], true, false),
        ["float2x2"] = new("float2x2 M = float2x2(1, 2, 3, 4);", "mul(uv, M).x", "M", [1f, 2f, 3f, 4f], false, false),
        ["float3x3"] = new("float3x3 M = float3x3(1, 2, 3, 4, 5, 6, 7, 8, 9);", "mul(float3(uv, 1), M).x", "M", [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f], false, false),
        ["row_major"] = new("row_major float2x2 M = float2x2(1, 2, 3, 4);", "mul(uv, M).x", "M", [1f, 2f, 3f, 4f], false, false),
        ["matrix-array"] = new("float2x2 MA[2] = { float2x2(1, 2, 3, 4), float2x2(5, 6, 7, 8) };", "mul(uv, MA[1]).x", "MA", [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f], false, false),
        ["int"] = new("int I = -3;", "(float)I", "I", [-3f], false, true),
        ["int2"] = new("int2 I2 = int2(4, -5);", "(float)I2.y", "I2", [4f, -5f], false, true),
        ["uint"] = new("uint U = 3000000000;", "(float)U", "U", [3000000000f], false, true),
        ["bool"] = new("bool B = true;", "(B ? 1 : 0)", "B", [1f], false, true),
        ["bool2"] = new("bool2 B2 = bool2(true, false);", "(B2.x ? 1 : 0)", "B2", [1f, 0f], false, true),
    };

    public static IEnumerable<object[]> ShapeNames => Shapes.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> RaylibShapeNames =>
        Shapes.Where(kv => kv.Value.OnRaylib).Select(kv => new object[] { kv.Key });

    private static readonly string[] GoldenDefaults =
    [
        "A_Tint.fx", "B_Scale.fx", "C_Vec2Mat2.fx", "D_IntBool.fx", "E_Array.fx", "F_NoInit.fx",
        "KitchenSink.fx", "MatrixDefault.fx", "RedDefault.fx", "Unmodelled.fx",
    ];

    public static IEnumerable<object[]> GoldenDefaultNames => GoldenDefaults.Select(n => new object[] { n });

    // ---------------------------------------------------------------------------------------------
    // Same values as the MGFX route
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public async Task Sksl_DefaultValue_EqualsTheMgfxRoutesParameterDefault(string shape)
    {
        Shape s = Shapes[shape];
        string fx = Fx(s.Declarations, s.Use);

        SkslConversion conversion = Sksl(fx);
        IReadOnlyList<ParameterReflection> mgfx = await MgfxParameters(fx, s.OnGl);

        SkslUniform uniform = conversion.Uniforms.Single(u => u.Name == s.Uniform);
        uniform.DefaultValue.ShouldNotBeNull();
        uniform.DefaultValue.ShouldBe(s.Expected);
        AssertSameAsMgfx(conversion.Uniforms.Select(u => (u.Name, u.DefaultValue)), mgfx);
        conversion.Warnings.ShouldNotContain(w => w.Code == "SD0017");
    }

    [Theory]
    [MemberData(nameof(RaylibShapeNames))]
    public async Task Raylib_DefaultValue_EqualsTheMgfxRoutesParameterDefault(string shape)
    {
        Shape s = Shapes[shape];
        string fx = Fx(s.Declarations, s.Use);

        RaylibShader shader = Raylib(fx);
        IReadOnlyList<ParameterReflection> mgfx = await MgfxParameters(fx, s.OnGl);

        RaylibUniform uniform = shader.Uniforms.Single(u => u.Name == s.Uniform);
        uniform.DefaultValue.ShouldNotBeNull();
        uniform.DefaultValue.ShouldBe(s.Expected);
        AssertSameAsMgfx(shader.Uniforms.Select(u => (u.Name, u.DefaultValue)), mgfx);
        shader.Warnings.ShouldNotContain(w => w.Code == "SD0017");
    }

    [Fact]
    public void Raylib_MatrixShapes_AreStillRefused_SoTheyCarryNoDefault()
    {
        foreach ((string name, Shape s) in Shapes.Where(kv => !kv.Value.OnRaylib))
        {
            var result = RaylibConverter.Convert(Fx(s.Declarations, s.Use), new RaylibConvertOptions { SourceName = name + ".fx" });
            result.IsFailure.ShouldBeTrue(name);
            result.Error.ShouldContain(e => e.Code == "SD0636", name);
        }
    }

    /// <summary>
    /// The initializer-defaults golden corpus the MGFX route's rung-4 drivers render
    /// (<c>tests/fixtures/golden/initializer-defaults</c>): every uniform each converter reports has
    /// the default the MGFX route reflects for the same file.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenDefaultNames))]
    public async Task GoldenDefaultsCorpus_BothRoutes_EqualTheMgfxRoute(string file)
    {
        string path = SkslConverterTests.FindFixture("..", "golden", "initializer-defaults", file);
        string fx = File.ReadAllText(path);
        IReadOnlyList<ParameterReflection> mgfx = await MgfxParametersAnyTarget(fx, path);

        SkslConversion sksl = Sksl(fx, path);
        sksl.Uniforms.Count.ShouldBeGreaterThan(0, file);
        AssertSameAsMgfx(sksl.Uniforms.Select(u => (u.Name, u.DefaultValue)), mgfx);

        var raylib = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = path });
        if (raylib.IsFailure)
        {
            // Only the matrix fixtures may be refused, and only for their matrix.
            raylib.Error.ShouldAllBe(e => e.Code == "SD0636", file);
            Regex.IsMatch(fx, @"\bfloat\dx\d\b").ShouldBeTrue(file + " is refused by raylib but declares no matrix");
        }
        else
        {
            AssertSameAsMgfx(raylib.Value.Uniforms.Select(u => (u.Name, u.DefaultValue)), mgfx);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Null, and the SD0017 warning
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NoInitializer_IsNull_OnBothRoutes()
    {
        string fx = Fx("float Thickness; float4 Col;", "Thickness + Col.x");

        Sksl(fx).Uniforms.ShouldAllBe(u => u.DefaultValue == null);
        Raylib(fx).Uniforms.ShouldAllBe(u => u.DefaultValue == null);
    }

    [Fact]
    public async Task UnevaluableInitializer_IsNull_WithTheMgfxRoutesSd0017Warning()
    {
        // DXC compiles dot() in a global initializer; the initializer reader does not model it.
        string fx = Fx("float Good = 0.5;\nfloat Q = dot(float2(1, 2), float2(3, 4));", "Good + Q");

        var mgfx = await new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Q.fx" });
        mgfx.IsSuccess.ShouldBeTrue();
        ShaderError expected = mgfx.Value.Warnings.Single(w => w.Code == "SD0017");

        SkslConversion sksl = Sksl(fx, "Q.fx");
        sksl.Uniforms.Single(u => u.Name == "Q").DefaultValue.ShouldBeNull();
        sksl.Uniforms.Single(u => u.Name == "Good").DefaultValue.ShouldBe([0.5f]);
        ShaderError skslWarning = sksl.Warnings.Single(w => w.Code == "SD0017");
        skslWarning.ShouldBe(expected);
        skslWarning.Message.ShouldContain("'Q'", Case.Sensitive);
        skslWarning.Severity.ShouldBe(ShaderErrorSeverity.Warning);

        RaylibShader raylib = Raylib(fx, "Q.fx");
        raylib.Uniforms.Single(u => u.Name == "Q").DefaultValue.ShouldBeNull();
        raylib.Uniforms.Single(u => u.Name == "Good").DefaultValue.ShouldBe([0.5f]);
        raylib.Warnings.Single(w => w.Code == "SD0017").ShouldBe(expected);
    }

    [Fact]
    public void Sksl_SynthesizedUniforms_AreListedWithANullDefault_AndChildrenAreNotListed()
    {
        // Gum's Grayscale reads COLOR0 (ShadowDusk_Color); a computed-coordinate sample adds
        // ShadowDusk_Resolution. Neither has an HLSL declaration, so neither has a default.
        const string fx = """
            Texture2D SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };
            float Thickness = 1.0;
            float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR
            {
                return tex2D(SpriteTextureSampler, uv * 0.5) * color * Thickness;
            }
            technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
            """;

        SkslConversion conversion = Sksl(fx);

        conversion.SynthesizedUniforms.ShouldBe(["ShadowDusk_Color", "ShadowDusk_Resolution"], ignoreOrder: true);
        foreach (string synthesized in conversion.SynthesizedUniforms)
            conversion.Uniforms.Single(u => u.Name == synthesized).DefaultValue.ShouldBeNull();
        conversion.Uniforms.Single(u => u.Name == "Thickness").DefaultValue.ShouldBe([1f]);
        conversion.ChildShaders.ShouldBe(["SpriteTexture"]);
        conversion.Uniforms.ShouldNotContain(u => u.Name == "SpriteTexture");
        conversion.Uniforms.ShouldNotContain(u => u.SkslType == "shader");

        // The list is exactly what Skia's own compiler sees, in its order.
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull(errors);
        conversion.Uniforms.Select(u => u.Name).ShouldBe(effect.Uniforms.ToArray());
    }

    [Fact]
    public async Task Sksl_ReservedWordGlobal_RenamedBySpirvCross_ReportsTheDeclaredDefault()
    {
        // SPIRV-Cross renames a GLSL reserved word ('input' becomes '_input'); the SkSL uniform is
        // set under the new name, and its default is the one 'input' declares.
        string fx = Fx("float input = 2;", "input");

        SkslConversion conversion = Sksl(fx);
        IReadOnlyList<ParameterReflection> mgfx = await MgfxParameters(fx, onGl: true);

        conversion.Uniforms.Single().Name.ShouldBe("_input");
        conversion.Uniforms.Single().DefaultValue.ShouldBe([2f]);
        mgfx.Single(p => p.Name == "input").DefaultValue.ShouldBe([2f]);
    }

    /// <summary>
    /// Every SPIRV-Cross rename is paired back to its HLSL global through the SPIR-V member names,
    /// never guessed from the spelling: a double underscore collapsed, a <c>gl_</c> prefix fixed
    /// up, and a reserved word that clashes with another global's name. The MGFX side is compiled
    /// for OpenGL: the clash used to fail there with an internal SD0012 (issue #440), so it was
    /// checked against DirectX only.
    /// </summary>
    [Theory]
    [InlineData("float my__val = 0.5;", "my__val", 0.5f)]
    [InlineData("float gl_Thing = 0.5;", "gl_Thing", 0.5f)]
    [InlineData("float _input = 0.25;\nfloat input = 0.5;", "_input", 0.25f)]
    [InlineData("float _input = 0.25;\nfloat input = 0.5;", "input", 0.5f)]
    public async Task Sksl_SpirvCrossRenames_ArePairedByTheSpirvMemberNames(string declarations, string hlslName, float expected)
    {
        string use = string.Join(" + ", Regex.Matches(declarations, @"float (\w+) =").Select(m => m.Groups[1].Value));
        string fx = Fx(declarations, use);

        SkslConversion conversion = Sksl(fx);
        IReadOnlyList<ParameterReflection> mgfx = await MgfxParameters(fx, onGl: true);

        mgfx.Single(p => p.Name == hlslName).DefaultValue.ShouldBe([expected]);
        conversion.Uniforms.Count(u => u.DefaultValue is { } d && d.SequenceEqual([expected])).ShouldBe(1,
            string.Join(", ", conversion.Uniforms.Select(u => $"{u.Name}=[{(u.DefaultValue is null ? "null" : string.Join(",", u.DefaultValue))}]")));
        conversion.Uniforms.ShouldAllBe(u => u.DefaultValue != null);
        conversion.Warnings.ShouldNotContain(w => w.Code == "SD0017");
    }

    [Fact]
    public void Sksl_ClashingReservedWordNames_EachGetTheirOwnDefault()
    {
        SkslConversion conversion = Sksl(Fx("float _input = 0.25;\nfloat input = 0.5;", "_input + input"));

        // SPIRV-Cross keeps `_input` and renames `input` to the next free name.
        conversion.Uniforms.Single(u => u.Name == "_input").DefaultValue.ShouldBe([0.25f]);
        SkslUniform renamed = conversion.Uniforms.Single(u => u.Name != "_input");
        renamed.Name.ShouldNotBe("input");
        renamed.DefaultValue.ShouldBe([0.5f]);
    }

    [Fact]
    public void Sksl_RenamedGlobalWithoutAnInitializer_IsNull_BesideAStaticOfTheRenamedSpelling()
    {
        // `input` (no initializer) becomes `_input`; the static `_input` is not a uniform.
        SkslConversion conversion = Sksl(Fx("float input;\nstatic float _input = 0.75;", "input + _input"));

        conversion.Uniforms.Single().DefaultValue.ShouldBeNull();
        conversion.Warnings.ShouldNotContain(w => w.Code == "SD0017");
    }

    [Fact]
    public void AGlobalWithADefault_ThatNoConvertedUniformPairsWith_IsAnSd0017Warning()
    {
        // The resolver is handed a uniform list missing one global (as for a declaration shape
        // the converter's uniform scan does not list): its default must be reported as lost.
        string fx = Fx("float A = 0.5;\nfloat B = 0.25;", "A + B");
        SkslConversion conversion = Sksl(fx);
        conversion.Uniforms.Select(u => u.Name).ShouldBe(["A", "B"]);

        // The converter's own front half: pre-parse, flatten with the OpenGL macros, strip techniques.
        var parse = ShadowDusk.HLSL.FxPreParser.Parse(fx, "AB.fx");
        parse.IsSuccess.ShouldBeTrue();
        var flattened = ShadowDusk.Compiler.Internal.ModernGlslSeam.Flatten(parse.Value.StrippedHlsl, "AB.fx", null, []);
        flattened.IsSuccess.ShouldBeTrue();
        string input = ShadowDusk.HLSL.FxPreParser.StripTechniqueBlocks(flattened.Value.Text, "AB.fx");
        var seam = ShadowDusk.Compiler.Internal.ModernGlslSeam.CompilePixel(input, "main", "AB.fx", default);
        seam.IsSuccess.ShouldBeTrue();

        var warnings = new List<ShaderError>();
        IReadOnlyList<float>?[] defaults = ShadowDusk.Compiler.Internal.ConvertedUniformDefaults.Resolve(
            input, "AB.fx", seam.Value.Glsl, seam.Value.Spirv,
            [new ShadowDusk.Compiler.Internal.ConvertedUniformDefaults.Uniform("A", "float", 0)], [], warnings);

        defaults.Single().ShouldBe([0.5f]);
        ShaderError lost = warnings.Single(w => w.Code == "SD0017");
        lost.Message.ShouldContain("'B'", Case.Sensitive);
        lost.Message.ShouldContain("no uniform ShadowDusk could pair", Case.Sensitive);
    }

    /// <summary>
    /// The degenerate one-row / one-column shapes, where the converted uniform's shape and the
    /// MGFX parameter's differ, pinned as documented: <c>float1</c> is a plain <c>float</c> uniform
    /// with its value; <c>float1x1</c>, <c>float1x3</c> and <c>float3x1</c> are not a matrix in the
    /// converted shader, so they report null with SD0017, never a wrongly shaped value.
    /// </summary>
    [Theory]
    [InlineData("float1 F = 0.5;", "F.x", new[] { 0.5f })]
    [InlineData("float1x1 F = 0.5;", "F._m00", null)]
    [InlineData("float1x3 F = float1x3(0.5, 0.25, 0.125);", "F._m00 + F._m02", null)]
    [InlineData("float3x1 F = float3x1(0.5, 0.25, 0.125);", "F._m00 + F._m20", null)]
    public void DegenerateShapes_FollowTheConvertedUniform(string declaration, string use, float[]? expected)
    {
        string fx = Fx(declaration, use);
        SkslConversion conversion = Sksl(fx);
        SkslUniform uniform = conversion.Uniforms.Single();

        if (expected is null)
        {
            uniform.DefaultValue.ShouldBeNull(uniform.SkslType);
            conversion.Warnings.ShouldContain(w => w.Code == "SD0017");
        }
        else
        {
            uniform.DefaultValue.ShouldBe(expected);
            conversion.Warnings.ShouldNotContain(w => w.Code == "SD0017");
        }
    }

    [Fact]
    public void SlangSubsetRoute_ReportsTheDeclaredDefault_OnBothConverters()
    {
        const string slang = """
            float Thickness = 1.5;
            float4 Tint = float4(0.5, 0.25, 1, 1);

            [shader("fragment")]
            float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
            {
                return Tint * Thickness;
            }
            """;

        var sksl = SkslConverter.ConvertSlang(slang, new SkslConvertOptions());
        sksl.IsSuccess.ShouldBeTrue(sksl.IsFailure ? string.Join(" | ", sksl.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        sksl.Value.Uniforms.Single(u => u.Name == "Thickness").DefaultValue.ShouldBe([1.5f]);
        sksl.Value.Uniforms.Single(u => u.Name == "Tint").DefaultValue.ShouldBe([0.5f, 0.25f, 1f, 1f]);

        var raylib = RaylibConverter.ConvertSlang(slang, new RaylibConvertOptions());
        raylib.IsSuccess.ShouldBeTrue(raylib.IsFailure ? string.Join(" | ", raylib.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        raylib.Value.Uniforms.Single(u => u.Name == "Thickness").DefaultValue.ShouldBe([1.5f]);
        raylib.Value.Uniforms.Single(u => u.Name == "Tint").DefaultValue.ShouldBe([0.5f, 0.25f, 1f, 1f]);
    }

    [Fact]
    public void UniformEquality_ComparesTheDefaultByValue()
    {
        new SkslUniform("A", "vec2", 0) { DefaultValue = [1f, 2f] }
            .ShouldBe(new SkslUniform("A", "vec2", 0) { DefaultValue = new List<float> { 1f, 2f } });
        new SkslUniform("A", "vec2", 0) { DefaultValue = [1f, 2f] }
            .ShouldNotBe(new SkslUniform("A", "vec2", 0) { DefaultValue = [1f, 3f] });
        new SkslUniform("A", "vec2", 0).ShouldNotBe(new SkslUniform("A", "vec2", 0) { DefaultValue = [0f, 0f] });

        new RaylibUniform("A", "vec2", 0) { DefaultValue = [1f, 2f] }
            .ShouldBe(new RaylibUniform("A", "vec2", 0) { DefaultValue = new List<float> { 1f, 2f } });
        new RaylibUniform("A", "vec2", 0) { DefaultValue = [1f, 2f] }
            .ShouldNotBe(new RaylibUniform("A", "vec2", 0) { DefaultValue = [1f, 3f] });
        new RaylibUniform("A", "vec2", 0).ShouldBe(new RaylibUniform("A", "vec2", 0));
    }

    // ---------------------------------------------------------------------------------------------
    // The shader text does not change
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Byte identity: the converted text of a source with initializers is exactly the text of the
    /// same source with every initializer removed. The default is reflection only.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void ConvertedText_IsByteIdentical_WithAndWithoutTheInitializer(string shape)
    {
        Shape s = Shapes[shape];
        string with = Fx(s.Declarations, s.Use);
        string without = Fx(StripInitializers(s.Declarations), s.Use);
        without.ShouldNotBe(with);

        SkslConversion skslWith = Sksl(with);
        SkslConversion skslWithout = Sksl(without);
        skslWith.SkslText.ShouldBe(skslWithout.SkslText);
        skslWithout.Uniforms.Single(u => u.Name == s.Uniform).DefaultValue.ShouldBeNull();

        if (s.OnRaylib)
            Raylib(with).FragmentShader.ShouldBe(Raylib(without).FragmentShader);
    }

    // ---------------------------------------------------------------------------------------------
    // Real Skia: setting the reported default renders the HLSL default; leaving it unset renders 0
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The layout claim, in real SkiaSharp: handing Skia the reported list as is renders what the
    /// HLSL computes from its declared default, for each matrix size (a transposed or reordered
    /// list renders a different colour: each matrix is asymmetric), arrays, a vector and an int;
    /// leaving the uniform at zero (what an unset uniform reads) renders the zero case.
    /// </summary>
    [Theory]
    [InlineData("float4x4")]
    [InlineData("float3x3")]
    [InlineData("float2x2")]
    [InlineData("row_major")]
    [InlineData("float-array")]
    [InlineData("vec4-array")]
    [InlineData("scalar")]
    [InlineData("int")]
    public void Skia_SettingTheReportedDefault_RendersTheHlslDefault_AndUnsetRendersZero(string shape)
    {
        (string declarations, string use, Func<float[]> expected) = SkiaCases[shape];
        SkslConversion conversion = Sksl(Header + declarations + "\n" +
            "float4 main(float2 uv : TEXCOORD0) : COLOR0 { return float4(" + use + ", 1); }\n" +
            "technique T { pass P { PixelShader = compile PS main(); } }\n");

        float[] rgb = expected();
        SKColor withDefaults = RenderCenter(conversion, u => u.DefaultValue!.ToArray());
        AssertPixel(withDefaults, rgb, shape + " with the reported defaults");

        SKColor unset = RenderCenter(conversion, u => new float[u.DefaultValue!.Count]);
        AssertPixel(unset, [0f, 0f, 0f], shape + " left unset (zeros)");
        Distance(withDefaults, unset).ShouldBeGreaterThan(8, shape + ": the default must change the picture, or this proves nothing");
    }

    /// <summary>
    /// The control for the matrix layout claim: the same default handed to Skia transposed
    /// (column by column, the order of the blob in an <c>.mgfx</c>) renders a different colour, so
    /// the render above can tell the two layouts apart.
    /// </summary>
    [Theory]
    [InlineData("float4x4", 4)]
    [InlineData("float3x3", 3)]
    [InlineData("float2x2", 2)]
    public void Skia_TheMatrixDefaultTransposed_RendersADifferentColour(string shape, int n)
    {
        (string declarations, string use, Func<float[]> expected) = SkiaCases[shape];
        SkslConversion conversion = Sksl(Header + declarations + "\n" +
            "float4 main(float2 uv : TEXCOORD0) : COLOR0 { return float4(" + use + ", 1); }\n" +
            "technique T { pass P { PixelShader = compile PS main(); } }\n");

        SKColor right = RenderCenter(conversion, u => u.DefaultValue!.ToArray());
        SKColor transposed = RenderCenter(conversion, u =>
        {
            float[] m = u.DefaultValue!.ToArray();
            return Enumerable.Range(0, m.Length).Select(i => m[(i % n) * n + i / n]).ToArray();
        });

        AssertPixel(right, expected(), shape);
        Distance(right, transposed).ShouldBeGreaterThan(8, shape + ": a transposed matrix must render differently");
    }

    private static int Distance(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);

    // Each case returns an rgb in [0, 1]; the expectation is the HLSL math on the declared default.
    private static readonly Dictionary<string, (string Declarations, string Use, Func<float[]> Expected)> SkiaCases = new(StringComparer.Ordinal)
    {
        // mul(v, M)[c] = sum_r v[r] * M[r][c], with M[r][c] = (4r + c + 1) / 64.
        ["float4x4"] = ("float4x4 M = float4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16) / 64.0;",
            "mul(float4(0.5, 0.25, 0.75, 1), M).xyz",
            () => RowVectorTimes([0.5f, 0.25f, 0.75f, 1f], 4, 4, (r, c) => (4 * r + c + 1) / 64f)[..3]),
        ["float3x3"] = ("float3x3 M = float3x3(1, 2, 3, 4, 5, 6, 7, 8, 9) / 24.0;",
            "mul(float3(0.25, 0.5, 1), M)",
            () => RowVectorTimes([0.25f, 0.5f, 1f], 3, 3, (r, c) => (3 * r + c + 1) / 24f)),
        ["float2x2"] = ("float2x2 M = float2x2(0.1, 0.6, 0.3, 0.2);",
            "mul(float2(1, 0.5), M), 0",
            () => [.. RowVectorTimes([1f, 0.5f], 2, 2, (r, c) => new[] { 0.1f, 0.6f, 0.3f, 0.2f }[2 * r + c]), 0f]),
        ["row_major"] = ("row_major float2x2 M = float2x2(0.1, 0.6, 0.3, 0.2);",
            "mul(float2(1, 0.5), M), 0",
            () => [.. RowVectorTimes([1f, 0.5f], 2, 2, (r, c) => new[] { 0.1f, 0.6f, 0.3f, 0.2f }[2 * r + c]), 0f]),
        ["float-array"] = ("float W[3] = { 0.5, 0.25, 0.125 };", "W[0], W[1], W[2]", () => [0.5f, 0.25f, 0.125f]),
        ["vec4-array"] = ("float4 C[2] = { float4(0.1, 0.2, 0.3, 1), float4(0.4, 0.3, 0.2, 1) };", "C[0].xyz + C[1].xyz",
            () => [0.5f, 0.5f, 0.5f]),
        ["scalar"] = ("float Thickness = 1.0;", "Thickness * 0.75, 0, 0", () => [0.75f, 0f, 0f]),
        ["int"] = ("int Mode = 3;", "Mode / 4.0, 0, 0", () => [0.75f, 0f, 0f]),
    };

    private static float[] RowVectorTimes(float[] v, int rows, int columns, Func<int, int, float> m)
    {
        var result = new float[columns];
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
                result[c] += v[r] * m(r, c);
        return result;
    }

    private static SKColor RenderCenter(SkslConversion conversion, Func<SkslUniform, float[]> valueOf)
    {
        const int size = 8;
        using SKRuntimeEffect? effect = SKRuntimeEffect.CreateShader(conversion.SkslText, out string errors);
        effect.ShouldNotBeNull($"Skia rejected the SkSL:\n{errors}\n{conversion.SkslText}");

        // SKRuntimeEffectUniforms does not zero its buffer, so every uniform is written.
        var uniforms = new SKRuntimeEffectUniforms(effect);
        foreach (SkslUniform u in conversion.Uniforms)
        {
            if (u.Name == SkslGlslMapper.ResolutionUniform)
            {
                uniforms[u.Name] = new[] { (float)size, size };
                continue;
            }
            u.DefaultValue.ShouldNotBeNull(u.Name);
            float[] values = valueOf(u);
            // SkiaSharp takes a scalar uniform as a scalar, everything else as an array.
            bool isInt = u.SkslType is "int" || u.SkslType.StartsWith("ivec", StringComparison.Ordinal);
            if (isInt)
                uniforms[u.Name] = values.Length == 1 ? (int)values[0] : values.Select(v => (int)v).ToArray();
            else
                uniforms[u.Name] = values.Length == 1 ? values[0] : values;
        }

        using SKShader shader = effect.ToShader(uniforms, new SKRuntimeEffectChildren(effect));
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src })
            canvas.DrawRect(new SKRect(0, 0, size, size), paint);
        return bitmap.GetPixel(size / 2, size / 2);
    }

    private static void AssertPixel(SKColor pixel, float[] rgb, string what)
    {
        byte[] expected = rgb.Select(c => (byte)Math.Round(Math.Clamp(c, 0f, 1f) * 255)).ToArray();
        int d = Math.Max(Math.Abs(pixel.Red - expected[0]), Math.Max(Math.Abs(pixel.Green - expected[1]), Math.Abs(pixel.Blue - expected[2])));
        d.ShouldBeLessThanOrEqualTo(2,
            $"{what}: expected ({expected[0]}, {expected[1]}, {expected[2]}), rendered ({pixel.Red}, {pixel.Green}, {pixel.Blue})");
    }

    // ---------------------------------------------------------------------------------------------

    private static SkslConversion Sksl(string fx, string sourceName = "Defaults.fx")
    {
        var result = SkslConverter.Convert(fx, new SkslConvertOptions { SourceName = sourceName });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value;
    }

    private static RaylibShader Raylib(string fx, string sourceName = "Defaults.fx")
    {
        var result = RaylibConverter.Convert(fx, new RaylibConvertOptions { SourceName = sourceName });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value;
    }

    private static async Task<IReadOnlyList<ParameterReflection>> MgfxParameters(string fx, bool onGl, string sourceName = "Defaults.fx")
    {
        var result = await new EffectCompiler().CompileAsync(fx, new CompilerOptions
        {
            Target = onGl ? PlatformTarget.OpenGL : PlatformTarget.DirectX,
            SourceFileName = sourceName,
        });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "");
        return result.Value.Parameters;
    }

    // OpenGL first; the fixtures whose types OpenGL does not model (int/bool, float2x2) are DirectX ones.
    private static async Task<IReadOnlyList<ParameterReflection>> MgfxParametersAnyTarget(string fx, string sourceName)
    {
        var gl = await new EffectCompiler().CompileAsync(fx, new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = sourceName });
        if (gl.IsSuccess)
            return gl.Value.Parameters;
        gl.Error.ShouldAllBe(e => e.Code == "SD0210", "the OpenGL compile may only refuse a type it does not model");
        return await MgfxParameters(fx, onGl: false, sourceName);
    }

    /// <summary>
    /// Every uniform the route reports that the MGFX route also reflects has the MGFX route's
    /// default, element for element (null where the MGFX route has null). A leading underscore is
    /// SPIRV-Cross's reserved-word rename.
    /// </summary>
    private static void AssertSameAsMgfx(IEnumerable<(string Name, IReadOnlyList<float>? Default)> uniforms, IReadOnlyList<ParameterReflection> mgfx)
    {
        int compared = 0;
        foreach ((string name, IReadOnlyList<float>? value) in uniforms)
        {
            ParameterReflection? p = mgfx.FirstOrDefault(x => x.Name == name)
                ?? (name.StartsWith('_') ? mgfx.FirstOrDefault(x => x.Name == name[1..]) : null);
            if (p is null)
                continue;
            compared++;
            if (p.DefaultValue is null)
                value.ShouldBeNull(name);
            else
                value.ShouldBe(p.DefaultValue, name);
        }
        compared.ShouldBeGreaterThan(0, "no reported uniform matched an MGFX parameter, so nothing was compared");
    }

    // `float A = 1;` -> `float A;`, statement by statement; `static const` lines are kept.
    private static string StripInitializers(string declarations) =>
        string.Join("\n", declarations.Split('\n').Select(line =>
            line.Contains("static", StringComparison.Ordinal) || line.StartsWith('#')
                ? line
                : Regex.Replace(line, @"\s*=[^;]*;", ";")));
}
