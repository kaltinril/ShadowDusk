#nullable enable

using ShadowDusk.Compiler.Raylib;
using ShadowDusk.Compiler.Sksl;
using ShadowDusk.Compiler.Tests.Sksl;
using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pins <see cref="ShaderConverter"/>, the extension-free entry point: each output kind routes an
/// <c>.fx</c> or Slang source found by content alone to the same result the format-specific
/// entry point gives, an input it does not read fails with <see cref="ShaderErrorKind.UnsupportedInput"/>
/// and nothing else, and a recognized but broken shader still fails with its real error.
/// Needs the DXC + SPIRV-Cross natives this suite's lanes always carry.
/// </summary>
public sealed class ShaderConverterTests
{
    private static readonly string FxSource =
        File.ReadAllText(SkslConverterTests.FindFixture("Grayscale.fx"));

    private static readonly string SlangSource =
        File.ReadAllText(SkslConverterTests.FindFixture("slang", "Invert.slang"));

    private const string ShaderToySource = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

    private const string RaylibGlslSource = """
        #version 330
        in vec2 fragTexCoord;
        uniform sampler2D texture0;
        out vec4 finalColor;
        void main() { finalColor = texture(texture0, fragTexCoord); }
        """;

    private const string BrokenFx = """
        float4 MainPS(float2 uv : TEXCOORD0) : COLOR { return undeclaredThing; }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    public static TheoryData<string> NotMine => new()
    {
        "",
        "float x = 1.0;\n",
        "{ \"not\": \"a shader\" }",
        ShaderToySource,
        RaylibGlslSource,
    };

    // ── CanConvert ──────────────────────────────────────────────────────────────────

    [Fact]
    public void CanConvert_FxAndSlang_True()
    {
        ShaderConverter.CanConvert(FxSource).ShouldBeTrue();
        ShaderConverter.CanConvert(SlangSource).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(NotMine))]
    public void CanConvert_InputItDoesNotRead_False(string source) =>
        ShaderConverter.CanConvert(source).ShouldBeFalse();

    [Fact]
    public void CanConvert_Ambiguous_True_SoTheLoudErrorIsReported() =>
        ShaderConverter.CanConvert(BrokenFx + "\n" + ShaderToySource).ShouldBeTrue();

    // ── MonoGame / KNI / FNA ────────────────────────────────────────────────────────

    [Fact]
    public void Compile_Fx_ByContent_MatchesEffectCompiler()
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL };

        var auto = ShaderConverter.Compile(compiler, FxSource, options);
        var direct = compiler.Compile(FxSource, options);

        auto.IsSuccess.ShouldBeTrue(Describe(auto));
        auto.Value.Data.ShouldBe(direct.Value.Data);
    }

    [Fact]
    public async Task CompileAsync_Slang_ByContent_MatchesTheSlangFrontendRoute()
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL };

        var auto = await ShaderConverter.CompileAsync(compiler, SlangSource, options);
        var fx = ShadowDusk.Compiler.Slang.SlangFrontend.ConvertToFx(
            SlangSource, new ShadowDusk.Compiler.Slang.SlangConvertOptions());
        var direct = await compiler.CompileAsync(fx.Value.FxText, options);

        auto.IsSuccess.ShouldBeTrue(Describe(auto));
        auto.Value.Data.ShouldBe(direct.Value.Data);
    }

    [Theory]
    [MemberData(nameof(NotMine))]
    public void Compile_InputItDoesNotRead_IsUnsupportedInput(string source)
    {
        var result = ShaderConverter.Compile(new EffectCompiler(), source, new CompilerOptions());

        ShaderError error = result.Error.Single();
        error.Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);
        error.Code.ShouldBe(ShaderConverter.UnsupportedInputCode);
        error.Code.ShouldBe("SD0650");
    }

    [Fact]
    public void Compile_GlslInput_NamesTheShaderToyPackage()
    {
        var result = ShaderConverter.Compile(new EffectCompiler(), ShaderToySource, new CompilerOptions(), "toy.glsl");

        ShaderError error = result.Error.Single();
        error.Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);
        error.File.ShouldBe("toy.glsl");
        error.Message.ShouldContain("ShadowDusk.ShaderToy", Case.Sensitive);
    }

    [Fact]
    public void Compile_RecognizedButBroken_IsACompileError_NotUnsupported()
    {
        var result = ShaderConverter.Compile(
            new EffectCompiler(), BrokenFx, new CompilerOptions { Target = PlatformTarget.OpenGL });

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeEmpty();
        result.Error.ShouldAllBe(e => e.Kind != ShaderErrorKind.UnsupportedInput);
    }

    [Fact]
    public void Compile_SlangOnlyConstruct_IsTheFrontendsSd0600_NotUnsupported()
    {
        var result = ShaderConverter.Compile(
            new EffectCompiler(), "import common;\n" + SlangSource, new CompilerOptions());

        ShaderError error = result.Error.Single();
        error.Code.ShouldBe("SD0600");
        error.Kind.ShouldBe(ShaderErrorKind.Compile);
    }

    [Fact]
    public void Compile_Ambiguous_IsAmbiguousInput_AndAHintSettlesIt()
    {
        string source = FxSource + "\n" + ShaderToySource;

        ShaderError error = ShaderConverter.Compile(new EffectCompiler(), source, new CompilerOptions()).Error.Single();
        error.Kind.ShouldBe(ShaderErrorKind.AmbiguousInput);
        error.Code.ShouldBe("SD0005");

        // With a .glsl hint the GLSL side wins, which this package reports as unsupported.
        ShaderConverter.Compile(new EffectCompiler(), source, new CompilerOptions(), "x.glsl")
            .Error.Single().Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);
    }

    // ── SkiaSharp ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ConvertToSksl_FxAndSlang_ByContent_MatchTheDirectConverters()
    {
        var options = new SkslConvertOptions();

        var fx = ShaderConverter.ConvertToSksl(FxSource, options);
        fx.IsSuccess.ShouldBeTrue(Describe(fx));
        fx.Value.SkslText.ShouldBe(SkslConverter.Convert(FxSource, options).Value.SkslText);

        var slang = ShaderConverter.ConvertToSksl(SlangSource, options);
        slang.IsSuccess.ShouldBeTrue(Describe(slang));
        slang.Value.SkslText.ShouldBe(SkslConverter.ConvertSlang(SlangSource, options).Value.SkslText);
    }

    [Theory]
    [MemberData(nameof(NotMine))]
    public void ConvertToSksl_InputItDoesNotRead_IsUnsupportedInput(string source) =>
        ShaderConverter.ConvertToSksl(source, new SkslConvertOptions())
            .Error.Single().Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);

    // ── raylib ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConvertToRaylib_FxAndSlang_ByContent_MatchTheDirectConverters()
    {
        var options = new RaylibConvertOptions();

        var fx = ShaderConverter.ConvertToRaylib(FxSource, options);
        fx.IsSuccess.ShouldBeTrue(Describe(fx));
        fx.Value.FragmentShader.ShouldBe(RaylibConverter.Convert(FxSource, options).Value.FragmentShader);

        var slang = ShaderConverter.ConvertToRaylib(SlangSource, options);
        slang.IsSuccess.ShouldBeTrue(Describe(slang));
        slang.Value.FragmentShader.ShouldBe(RaylibConverter.ConvertSlang(SlangSource, options).Value.FragmentShader);
    }

    [Fact]
    public void ConvertToRaylib_RaylibGlslFile_IsUnsupported_SoRaylibCanLoadItDirectly() =>
        ShaderConverter.ConvertToRaylib(RaylibGlslSource, new RaylibConvertOptions())
            .Error.Single().Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);

    [Fact]
    public void ConvertToRaylib_RecognizedButBroken_IsNotUnsupported()
    {
        var result = ShaderConverter.ConvertToRaylib(BrokenFx, new RaylibConvertOptions());

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldAllBe(e => e.Kind != ShaderErrorKind.UnsupportedInput);
    }

    private static string Describe<T>(Result<T, ShaderError[]> result) =>
        result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "";
}
