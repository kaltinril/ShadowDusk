#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #252 follow-up: which registers the author wrote is decided from slangc's own
/// preprocess-only output (<c>-E</c>), not from the raw source text. These drive
/// <see cref="SlangCompiler"/> with a FAKE slangc on both transports (the in-process seam and
/// the process seam), so they pin the wiring on every host: when the pass runs, the exact
/// argument list it gets, that its answer (not the raw text) decides the strip, and how a
/// failure surfaces. Pure: no disk, no process. The real-slangc proof is
/// <see cref="SlangGlSamplerSlotTests"/>.
/// </summary>
public sealed class SlangRegisterPreprocessPassTests
{
    private const string FakeSlangcPath = "fake-tools/slangc";

    // The register sits in the '#else' branch: live for DirectX, dead for OpenGL.
    private const string BranchSource = """
        Texture2D SpriteTexture;
        #if OPENGL
        SamplerState SpriteSampler;
        #else
        SamplerState SpriteSampler : register(s0);
        #endif

        [shader("fragment")]
        float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return SpriteTexture.Sample(SpriteSampler, uv); }
        """;

    // What slangc emits for the entry either way: it numbers every resource itself.
    private const string Emission = """
        #pragma pack_matrix(column_major)

        #line 1 "<stdin>"
        Texture2D<float4 > SpriteTexture : register(t0);

        #line 3
        SamplerState SpriteSampler : register(s0);

        #line 9
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return SpriteTexture.Sample(SpriteSampler, uv_0);
        }

        """;

    private const string PreprocessedWithoutRegister =
        "Texture2D SpriteTexture ; SamplerState SpriteSampler ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return SpriteTexture . Sample ( SpriteSampler , uv ) ; } \n";

    private const string PreprocessedWithRegister =
        "Texture2D SpriteTexture ; SamplerState SpriteSampler : register ( s0 ) ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return SpriteTexture . Sample ( SpriteSampler , uv ) ; } \n";

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? Captured;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            Captured = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }

    /// <summary>A slangc that answers the preprocess-only list with <paramref name="preprocess"/>
    /// and every other list with <see cref="Emission"/>.</summary>
    private sealed class FakeSlangc((int ExitCode, string Stdout, string Stderr) preprocess)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public (int ExitCode, string Stdout, string Stderr) Run(string slangSource, IReadOnlyList<string> arguments)
        {
            Calls.Add(arguments);
            return arguments.Contains("-E") ? preprocess : (0, Emission, "");
        }
    }

    public enum Transport
    {
        InProcess,
        Process,
    }

    private static SlangCompiler Create(Transport transport, CapturingCompiler downstream, FakeSlangc slangc) =>
        transport == Transport.InProcess
            ? new SlangCompiler(downstream, slangc.Run)
            : new SlangCompiler(
                downstream,
                () => new SlangCompiler.SlangcLocation(null, FakeSlangcPath),
                prepareSlangc: path => path,
                runSlangc: (path, _, source, arguments) =>
                {
                    path.ShouldBe(FakeSlangcPath);
                    return slangc.Run(source, arguments);
                });

    private static CompilerOptions Options(PlatformTarget target) =>
        new() { Target = target, SourceFileName = "Branch.slang" };

    [Fact]
    public void BuildPreprocess_IsTheCompilesListWithDashEInPlaceOfTheEntry()
    {
        IReadOnlyList<string> args = SlangcArguments.BuildPreprocess(
            PlatformMacros.For(PlatformTarget.OpenGL).Macros, [new UserDefine("QUALITY", "2")]);

        args.ShouldBe(
        [
            "-lang", "slang",
            "-DMGFX=1", "-DGLSL=1", "-DOPENGL=1",
            "-DQUALITY=2",
            "-target", "hlsl",
            "-no-hlsl-pack-constant-buffer-elements",
            "-no-mangle",
            "-E",
            "--", "-",
        ]);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void RegisterOnlyInAnInactiveBranch_IsStripped(Transport transport)
    {
        // The raw text contains 'SpriteSampler : register(s0)', but slangc's preprocessor (for
        // OpenGL) never lets it reach the compile: the register in the emission is slangc's own.
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((0, PreprocessedWithoutRegister, ""));

        var result = Create(transport, downstream, slangc).Compile(BranchSource, Options(PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("SamplerState SpriteSampler;", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
        fx.ShouldNotContain("register(", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void RegisterInTheActiveBranch_IsKept(Transport transport)
    {
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((0, PreprocessedWithRegister, ""));

        var result = Create(transport, downstream, slangc).Compile(BranchSource, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > SpriteTexture;", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ThePass_RunsOnceAfterTheEntryCompiles_WithTheSharedPreprocessList(Transport transport)
    {
        var slangc = new FakeSlangc((0, PreprocessedWithoutRegister, ""));
        var options = new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "Branch.slang",
            Defines = [new UserDefine("QUALITY", "2")],
        };

        Create(transport, new CapturingCompiler(), slangc).Compile(BranchSource, options).IsSuccess.ShouldBeTrue();

        IReadOnlyList<MacroDefinition> macros = PlatformMacros.For(PlatformTarget.OpenGL).Macros;
        slangc.Calls.Count.ShouldBe(2);
        slangc.Calls[0].ShouldBe(SlangcArguments.Build(macros, options.Defines, "MainPS", "fragment"));
        slangc.Calls[1].ShouldBe(SlangcArguments.BuildPreprocess(macros, options.Defines));
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void SourceThatCannotSpellARegister_SkipsThePass(Transport transport)
    {
        const string source = """
            Texture2D SpriteTexture;
            SamplerState SpriteSampler;

            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return SpriteTexture.Sample(SpriteSampler, uv); }
            """;
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((1, "", "the preprocess pass must not run"));

        var result = Create(transport, downstream, slangc).Compile(source, Options(PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue();
        slangc.Calls.Count.ShouldBe(1);
        slangc.Calls[0].ShouldNotContain("-E");
        downstream.Captured.ShouldNotBeNull().ShouldNotContain("register(", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void RegisterCarriedByAUserDefine_RunsThePass(Transport transport)
    {
        // The source text never says 'register'; the -D value does.
        const string source = """
            Texture2D SpriteTexture;
            SamplerState SpriteSampler SLOT;

            [shader("fragment")]
            float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return SpriteTexture.Sample(SpriteSampler, uv); }
            """;
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((0, PreprocessedWithRegister, ""));
        var options = new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "Define.slang",
            Defines = [new UserDefine("SLOT", ": register(s0)")],
        };

        Create(transport, downstream, slangc).Compile(source, options).IsSuccess.ShouldBeTrue();

        slangc.Calls.Count.ShouldBe(2);
        downstream.Captured.ShouldNotBeNull().ShouldContain("SamplerState SpriteSampler : register(s0);", Case.Sensitive);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void FailedPass_SurfacesSlangcsOwnDiagnostic_AndNeverReachesDownstream(Transport transport)
    {
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((1, "", "error[E15300]: include file not found\n --> <stdin>:2:10\n"));

        var result = Create(transport, downstream, slangc).Compile(BranchSource, Options(PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe(("Branch.slang", 2, 10, "E15300"));
        error.Message.ShouldContain("include file not found", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void FailedPass_WithNoOutput_IsSD0622_NamingThePass(Transport transport)
    {
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((1, "", ""));

        var result = Create(transport, downstream, slangc).Compile(BranchSource, Options(PlatformTarget.OpenGL));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0622");
        error.Message.ShouldContain("preprocess-only pass (-E", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess, "")]
    [InlineData(Transport.InProcess, "  \n")]
    [InlineData(Transport.Process, "")]
    // Truncated: the entry point the compile just found is not in the output.
    [InlineData(Transport.Process, "Texture2D SpriteTexture ; SamplerState SpriteSampler : register ( s0 ) ; \n")]
    public void SuccessfulPass_WithUnusableOutput_IsSD0629_NeverASilentStrip(Transport transport, string output)
    {
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc((0, output, ""));

        var result = Create(transport, downstream, slangc).Compile(BranchSource, Options(PlatformTarget.DirectX));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        error.Code.ShouldBe("SD0629");
        error.Message.ShouldContain("will not guess", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }
}
