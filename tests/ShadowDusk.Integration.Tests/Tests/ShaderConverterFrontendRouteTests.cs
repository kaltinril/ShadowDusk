#nullable enable

using System.Reflection;
using System.Runtime.Loader;
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

    /// <summary>
    /// The claim "adding the package is the whole setup", proven where no other test can have
    /// touched a ShaderToy type first: a fresh AssemblyLoadContext gets its own copy of
    /// ShadowDusk.Core (its own registry) and nothing else, and the first Find must load
    /// ShadowDusk.ShaderToy and come back with its frontend.
    /// </summary>
    [Fact]
    public void Find_LoadsTheShaderToyPackage_ThatNothingHasTouched()
    {
        string directory = Path.GetDirectoryName(typeof(ShaderFrontends).Assembly.Location)!;
        var context = new IsolatedContext(directory);
        try
        {
            Assembly core = context.LoadFromAssemblyPath(Path.Combine(directory, "ShadowDusk.Core.dll"));
            core.ShouldNotBeSameAs(typeof(ShaderFrontends).Assembly);
            context.Assemblies.ShouldNotContain(a => a.GetName().Name == "ShadowDusk.ShaderToy");

            Type frontends = core.GetType("ShadowDusk.Core.ShaderFrontends", throwOnError: true)!;
            object glsl = Enum.Parse(core.GetType("ShadowDusk.Core.ShaderFormat", throwOnError: true)!, "Glsl");
            object? found = frontends.GetMethod("Find")!.Invoke(null, [glsl]);

            found.ShouldNotBeNull();
            found.GetType().FullName.ShouldBe("ShadowDusk.ShaderToy.ShaderToyFrontend");
            // Its own copies, loaded by the probe into the same context: not this test's.
            found.GetType().Assembly.ShouldNotBeSameAs(typeof(ShaderToyFrontend).Assembly);
            context.Assemblies.ShouldContain(a => a.GetName().Name == "ShadowDusk.ShaderToy");
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Find_ReturnsTheShaderToyFrontend_AndItsLoadTimeRegistrationNeverReplacesOne()
    {
        IShaderFrontend? frontend = ShaderFrontends.Find(ShaderFormat.Glsl);

        frontend.ShouldNotBeNull();
        frontend.ShouldBeOfType<ShaderToyFrontend>();
        frontend.Format.ShouldBe(ShaderFormat.Glsl);

        // What the package's module initializer calls: add only if absent. A frontend the
        // application registered itself must survive the package loading later.
        ShaderFrontends.TryRegister(new ShaderToyFrontend()).ShouldBeFalse();
        ShaderFrontends.Find(ShaderFormat.Glsl).ShouldBeSameAs(frontend);
    }

    [Fact]
    public void CanCompile_IsTrueForGlsl_WhileCanConvertStaysWhatEveryMethodReads()
    {
        ShaderConverter.CanCompile(ShaderToySource).ShouldBeTrue();
        // The SkSL and raylib methods do not read GLSL, so the check that covers them says no.
        ShaderConverter.CanConvert(ShaderToySource).ShouldBeFalse();
        ShaderConverter.CanCompile(FxSource).ShouldBeTrue();
        ShaderConverter.CanCompile("not a shader at all").ShouldBeFalse();
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Vulkan)]
    public async Task Compile_Glsl_EqualsConvertThenCompile_ByteForByte(PlatformTarget target)
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = target };

        var routed = ShaderConverter.Compile(compiler, ShaderToySource, options, "toy.glsl");
        var routedAsync = await ShaderConverter.CompileAsync(compiler, ShaderToySource, options, "toy.glsl");

        // The two steps by hand, named as the ShadowDuskCLI names a toy.glsl input. Vulkan
        // output embeds the source name, so it is part of the comparison there.
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
    [Trait("Category", "Integration")]
    public void Compile_Glsl_WithNoName_EqualsTheConvertersDefaults()
    {
        var compiler = new EffectCompiler();

        var routed = ShaderConverter.Compile(
            compiler, ShaderToySource, new CompilerOptions { Target = PlatformTarget.OpenGL });

        // ShaderToyConverter.Convert(source) with nothing else: same technique name, same bytes.
        ConvertResult converted = ShaderToyConverter.Convert(ShaderToySource);
        var separate = compiler.Compile(converted.Fx!, new CompilerOptions { Target = PlatformTarget.OpenGL });

        routed.IsSuccess.ShouldBeTrue(Describe(routed));
        separate.IsSuccess.ShouldBeTrue(Describe(separate));
        routed.Value.Data.ShouldBe(separate.Value.Data);
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

    [Theory]
    [InlineData("C:/game/Content/toy.glsl")]
    // A Windows path must give the same names on every host (the technique name is in the bytes).
    [InlineData(@"C:\game\Content\toy.glsl")]
    public void Compile_Glsl_FilesTheCompileUnderAGeneratedName_AndKeepsEveryOtherOption(string hint)
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "ignored-when-a-hint-is-given.glsl",
            EmbeddedSourceFileName = "embedded.fx",
            Debug = true,
            MgfxVersion = 11,
            Defines = [new UserDefine("QUALITY", "2")],
            AdditionalIncludePaths = ["inc"],
        };

        ShaderConverter.Compile(capture, ShaderToySource, options, hint);

        capture.Options.ShouldNotBeNull();
        // Compiler errors in generated HLSL must never carry the original file's name.
        capture.Options.SourceFileName.ShouldBe("toy.generated.fx");
        capture.Options.EmbeddedSourceFileName.ShouldBe("embedded.fx");
        capture.Options.Target.ShouldBe(PlatformTarget.DirectX);
        capture.Options.Debug.ShouldBeTrue();
        capture.Options.MgfxVersion.ShouldBe(11);
        capture.Options.Defines.ShouldBe(options.Defines);
        capture.Options.AdditionalIncludePaths.ShouldBe(options.AdditionalIncludePaths);
        capture.Source.ShouldNotBeNull();
        capture.Source.ShouldContain("technique toy", Case.Sensitive);
    }

    [Fact]
    public void Compile_Glsl_WithNoHint_TakesTheNameFromTheOptions()
    {
        var capture = new CapturingCompiler();

        ShaderConverter.Compile(
            capture, ShaderToySource, new CompilerOptions { SourceFileName = "Content/2d-noise.glsl" });

        capture.Options!.SourceFileName.ShouldBe("2d-noise.generated.fx");
        capture.Source!.ShouldContain("technique _2d_noise", Case.Sensitive);
    }

    [Fact]
    public void Compile_Glsl_WhenTheGeneratedFxFailsToCompile_SaysFirstThatTheErrorsAreInGeneratedHlsl()
    {
        var result = ShaderConverter.Compile(new CapturingCompiler(), ShaderToySource, new CompilerOptions(), "Content/toy.glsl");

        result.IsFailure.ShouldBeTrue();
        ShaderError note = result.Error[0];
        note.Code.ShouldBe("SD0003");
        note.Severity.ShouldBe(ShaderErrorSeverity.Note);
        note.File.ShouldBe("Content/toy.glsl");
        note.Message.ShouldContain("'toy.glsl'", Case.Sensitive);
        note.Message.ShouldContain("'toy.generated.fx'", Case.Sensitive);
        // The compiler's own errors follow, untouched.
        result.Error[^1].Code.ShouldBe("TEST");
    }

    [Fact]
    public void Compile_Fx_IsHandedOnUnchanged_WithTheCallersOptions_AndNoNoteOnFailure()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { SourceFileName = "plain.fx" };

        var result = ShaderConverter.Compile(capture, FxSource, options, "plain.fx");

        capture.Source.ShouldBe(FxSource);
        capture.Options.ShouldBeSameAs(options);
        result.Error.ShouldHaveSingleItem().Code.ShouldBe("TEST");
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

    /// <summary>Resolves only from one directory, so it shares no assembly with the default context.</summary>
    private sealed class IsolatedContext(string directory) : AssemblyLoadContext("issue-473-probe", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return assemblyName.Name is not null && assemblyName.Name.StartsWith("ShadowDusk.", StringComparison.Ordinal) && File.Exists(path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }

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
