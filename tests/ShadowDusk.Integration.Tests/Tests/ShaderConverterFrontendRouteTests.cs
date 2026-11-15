#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.ShaderToy;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #473, the package-PRESENT half (this test assembly references
/// <c>ShadowDusk.ShaderToy</c>): <see cref="ShaderConverter"/> routes ShaderToy / GLSL through
/// the frontend that package registers, with no wiring by the caller. The package-absent half
/// is <c>ShaderConverterTests</c> in <c>ShadowDusk.Compiler.Tests</c>, which does not reference
/// it and still gets <see cref="ShaderErrorKind.UnsupportedInput"/> for GLSL.
/// </summary>
public sealed class ShaderConverterFrontendRouteTests
{
    private const string ShaderToySource = """
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            vec2 uv = fragCoord / iResolution.xy;
            fragColor = vec4(uv, 0.5, 1.0);
        }
        """;

    // mainSound is outside the supported single-pass image subset: a conversion error.
    private const string RejectedShaderToySource = """
        vec2 mainSound(int samp, float time)
        {
            return vec2(0.0);
        }
        void mainImage(out vec4 fragColor, in vec2 fragCoord)
        {
            fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
        }
        """;

    private const string FxSource = """
        float4 MainPS(float2 uv : TEXCOORD0) : COLOR0 { return float4(uv, 0, 1); }
        technique T { pass P { PixelShader = compile ps_3_0 MainPS(); } }
        """;

    [Fact]
    public void ShaderFrontends_FindsTheShaderToyFrontend_WithNoRegistrationCall()
    {
        IShaderFrontend? frontend = ShaderFrontends.Find(ShaderFormat.Glsl);

        frontend.ShouldNotBeNull();
        frontend.ShouldBeOfType<ShaderToyFrontend>();
        frontend.Format.ShouldBe(ShaderFormat.Glsl);
    }

    [Fact]
    public void CanConvert_IsTrueForGlsl_OnceThePackageIsPresent() =>
        ShaderConverter.CanConvert(ShaderToySource).ShouldBeTrue();

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    public async Task Compile_Glsl_EqualsConvertThenCompile_ByteForByte(PlatformTarget target)
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = target };

        var routed = ShaderConverter.Compile(compiler, ShaderToySource, options, "toy.glsl");
        var routedAsync = await ShaderConverter.CompileAsync(compiler, ShaderToySource, options, "toy.glsl");

        // The same two steps a caller did by hand before the route existed.
        ConvertResult converted = ShaderToyConverter.Convert(
            ShaderToySource, new ConvertOptions { EffectName = "toy", TechniqueName = "toy" });
        converted.Success.ShouldBeTrue();
        var separate = compiler.Compile(
            converted.Fx!, new CompilerOptions { Target = target, SourceFileName = "toy.generated.fx" });

        separate.IsSuccess.ShouldBeTrue(Describe(separate));
        routed.IsSuccess.ShouldBeTrue(Describe(routed));
        routedAsync.IsSuccess.ShouldBeTrue(Describe(routedAsync));
        routed.Value.Data.ShouldBe(separate.Value.Data);
        routedAsync.Value.Data.ShouldBe(separate.Value.Data);
    }

    [Fact]
    public void Compile_Glsl_WithNoNameHint_Compiles()
    {
        var result = ShaderConverter.Compile(
            new EffectCompiler(), ShaderToySource, new CompilerOptions { Target = PlatformTarget.OpenGL });

        result.IsSuccess.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Compile_GlslTheFrontendRejects_IsTheFrontendsOwnError_LocatedInTheOriginal()
    {
        var result = ShaderConverter.Compile(
            new EffectCompiler(), RejectedShaderToySource, new CompilerOptions(), "sound.glsl");

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeEmpty();
        // A recognized-but-broken shader: never "not mine", which would send a chained loader on.
        result.Error.ShouldAllBe(e => e.Kind != ShaderErrorKind.UnsupportedInput);
        result.Error.ShouldAllBe(e => e.Code == ShaderToyFrontend.ConversionErrorCode);
        result.Error.ShouldAllBe(e => e.File == "sound.glsl");
        result.Error.ShouldContain(e => e.Line > 0);

        // Exactly the converter's own diagnostics, text included.
        ConvertResult direct = ShaderToyConverter.Convert(RejectedShaderToySource);
        result.Error.Select(e => (e.Line, e.Column)).ShouldBe(direct.Diagnostics.Select(d => (d.Line, d.Column)));
        foreach ((ShaderError error, ConvertDiagnostic diagnostic) in result.Error.Zip(direct.Diagnostics))
            error.Message.ShouldStartWith(diagnostic.Message, Case.Sensitive);
    }

    [Fact]
    public void Compile_Glsl_FilesTheCompileUnderAGeneratedName_AndKeepsEveryOtherOption()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "C:/game/Content/toy.glsl",
            Debug = true,
            Defines = [new UserDefine("QUALITY", "2")],
        };

        ShaderConverter.Compile(capture, ShaderToySource, options, "C:/game/Content/toy.glsl");

        capture.Options.ShouldNotBeNull();
        // Compiler errors in generated HLSL must never carry the original file's name.
        capture.Options.SourceFileName.ShouldBe("toy.generated.fx");
        capture.Options.Target.ShouldBe(PlatformTarget.DirectX);
        capture.Options.Debug.ShouldBeTrue();
        capture.Options.Defines.ShouldBe(options.Defines);
        capture.Source.ShouldNotBeNull();
        capture.Source.ShouldContain("technique toy", Case.Sensitive);
    }

    [Fact]
    public void Compile_Fx_IsHandedOnUnchanged_WithTheCallersOptions()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { SourceFileName = "plain.fx" };

        ShaderConverter.Compile(capture, FxSource, options, "plain.fx");

        capture.Source.ShouldBe(FxSource);
        capture.Options.ShouldBeSameAs(options);
    }

    [Fact]
    public void SkslAndRaylibMethods_StillReportGlslAsUnsupported()
    {
        ShaderConverter.ConvertToSksl(ShaderToySource, new ShadowDusk.Compiler.Sksl.SkslConvertOptions())
            .Error.Single().Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);
        ShaderConverter.ConvertToRaylib(ShaderToySource, new ShadowDusk.Compiler.Raylib.RaylibConvertOptions())
            .Error.Single().Kind.ShouldBe(ShaderErrorKind.UnsupportedInput);
    }

    private static string Describe(Result<CompiledShader, ShaderError[]> result) =>
        result.IsFailure ? string.Join("; ", result.Error.Select(e => e.FxcFormattedMessage)) : "";

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Source { get; private set; }

        public CompilerOptions? Options { get; private set; }

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Source = hlslSource;
            Options = options;
            return Result<CompiledShader, ShaderError[]>.Fail([new ShaderError("", 0, 0, "TEST", "captured")]);
        }

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
