#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #292: the wiring that decides registers on declarations from another file (an
/// imported module), driven through a FAKE slangc on both transports (the in-process seam the
/// browser uses and the process seam), so it is pinned on every host with no disk and no
/// process. The emission and <c>-E</c> texts below are slangc v2026.14.1's, verbatim in shape.
/// The real-slangc proof is <see cref="SlangForeignRegisterTests"/>.
/// </summary>
public sealed class SlangForeignRegisterPassTests
{
    private const string FakeSlangcPath = "fake-tools/slangc";
    private const string ModulePath = "C:/shaders/texmod.slang";

    private const string Source = """
        import "C:/shaders/texmod.slang";
        SamplerState S;

        [shader("fragment")]
        float4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return ModTex.Sample(S, uv) * ModNoReg.Sample(S, uv); }
        """;

    // slangc numbers every resource and names the file each came from.
    private const string Emission = """
        #pragma pack_matrix(column_major)

        #line 2 "C:/shaders/texmod.slang"
        Texture2D<float4 > ModTex : register(t3);

        #line 2 "<stdin>"
        SamplerState S : register(s0);

        #line 3 "C:/shaders/texmod.slang"
        Texture2D<float4 > ModNoReg : register(t0);

        #line 5 "<stdin>"
        float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
        {
            return ModTex.Sample(S, uv_0) * ModNoReg.Sample(S, uv_0);
        }

        """;

    // -E does not expand an import: the entry text has no ModTex declaration at all.
    private const string EntryPreprocessed =
        "import \"C:/shaders/texmod.slang\" ; SamplerState S ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return ModTex . Sample ( S , uv ) * ModNoReg . Sample ( S , uv ) ; } \n";

    private const string ModulePreprocessed =
        "module texmod ; public Texture2D ModTex : register ( t3 ) ; public Texture2D ModNoReg ; \n";

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

    /// <summary>Answers the compile with <paramref name="emission"/>, the entry source's -E with
    /// <paramref name="entry"/>, and a file's -E with <paramref name="files"/>[path].</summary>
    private sealed class FakeSlangc(
        string emission,
        string entry,
        IReadOnlyDictionary<string, (int ExitCode, string Stdout, string Stderr)> files)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public (int ExitCode, string Stdout, string Stderr) Run(string slangSource, IReadOnlyList<string> arguments)
        {
            Calls.Add(arguments);
            if (!arguments.Contains("-E"))
                return (0, emission, "");
            string input = arguments[^1];
            if (input == "-")
                return (0, entry, "");
            return files.TryGetValue(input, out var answer)
                ? answer
                : (0, "", $"error[E00001]: cannot open file '{input}'\n");
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
        new() { Target = target, SourceFileName = "Entry.slang" };

    [Fact]
    public void BuildPreprocessFile_IsThePreprocessListWithTheFileInPlaceOfStdin()
    {
        IReadOnlyList<MacroDefinition> macros = PlatformMacros.For(PlatformTarget.OpenGL).Macros;
        UserDefine[] defines = [new UserDefine("QUALITY", "2")];

        IReadOnlyList<string> file = SlangcArguments.BuildPreprocessFile(macros, defines, ModulePath);
        IReadOnlyList<string> stdin = SlangcArguments.BuildPreprocess(macros, defines);

        file.Take(file.Count - 1).ShouldBe(stdin.Take(stdin.Count - 1));
        file[^2].ShouldBe("--");
        file[^1].ShouldBe(ModulePath);
    }

    [Theory]
    [InlineData(Transport.InProcess, PlatformTarget.DirectX)]
    [InlineData(Transport.InProcess, PlatformTarget.OpenGL)]
    [InlineData(Transport.Process, PlatformTarget.DirectX)]
    [InlineData(Transport.Process, PlatformTarget.OpenGL)]
    public void ImportedModuleRegister_IsJudgedFromTheModulesOwnPass(Transport transport, PlatformTarget target)
    {
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc(Emission, EntryPreprocessed,
            new Dictionary<string, (int, string, string)> { [ModulePath] = (0, ModulePreprocessed, "") });

        var result = Create(transport, downstream, slangc).Compile(Source, Options(target));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > ModTex : register(t3);", Case.Sensitive);
        fx.ShouldContain("Texture2D<float4 > ModNoReg;", Case.Sensitive);
        fx.ShouldContain("SamplerState S;", Case.Sensitive);

        // One compile, the entry source's pass, then exactly one pass over the module, with the
        // compile's macros: the same lists on both transports.
        IReadOnlyList<MacroDefinition> macros = PlatformMacros.For(target).Macros;
        slangc.Calls.Count.ShouldBe(3);
        slangc.Calls[1].ShouldBe(SlangcArguments.BuildPreprocess(macros, []));
        slangc.Calls[2].ShouldBe(SlangcArguments.BuildPreprocessFile(macros, [], ModulePath));
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ImportOnlySource_StillRunsThePasses(Transport transport)
    {
        // The entry source never spells 'register' (the old skip), but the emission holds a
        // declaration from another file, which the entry text cannot speak for.
        var slangc = new FakeSlangc(Emission, EntryPreprocessed,
            new Dictionary<string, (int, string, string)> { [ModulePath] = (0, ModulePreprocessed, "") });

        SlangcRegisterStripper.MayWriteRegister(Source, []).ShouldBeFalse();
        Create(transport, new CapturingCompiler(), slangc).Compile(Source, Options(PlatformTarget.OpenGL)).IsSuccess.ShouldBeTrue();

        slangc.Calls.Count(c => c.Contains("-E")).ShouldBe(2);
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleThatCannotBeOpened_IsSD0628_WithSlangcsOwnWords(Transport transport)
    {
        // The browser's in-process slangc has no file system for an absolute import; there, and
        // anywhere else a module's pass cannot be read, -E exits 0 with an error on stderr.
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc(Emission, EntryPreprocessed, new Dictionary<string, (int, string, string)>());

        var result = Create(transport, downstream, slangc).Compile(Source, Options(PlatformTarget.DirectX));

        result.IsFailure.ShouldBeTrue();
        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Column, error.Code).ShouldBe((ModulePath, 2, 1, "SD0628"));
        error.Message.ShouldContain("'ModTex'", Case.Sensitive);
        error.Message.ShouldContain($"error[E00001]: cannot open file '{ModulePath}'", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void ModuleWhosePassCannotDecide_IsSD0628_NamingTheDeclaration(Transport transport)
    {
        // 'ModTex : MSLOT' where MSLOT is defined in a file this pass never sees.
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc(Emission, EntryPreprocessed,
            new Dictionary<string, (int, string, string)>
            {
                [ModulePath] = (0, "module texmod ; public Texture2D ModTex : MSLOT ; public Texture2D ModNoReg ; \n", ""),
            });

        var result = Create(transport, downstream, slangc).Compile(Source, Options(PlatformTarget.OpenGL));

        ShaderError error = result.Error.ShouldHaveSingleItem();
        (error.File, error.Line, error.Code).ShouldBe((ModulePath, 2, "SD0628"));
        error.Message.ShouldContain("'ModTex'", Case.Sensitive);
        error.Message.ShouldContain("will not guess", Case.Sensitive);
        downstream.Captured.ShouldBeNull();
    }

    [Theory]
    [InlineData(Transport.InProcess)]
    [InlineData(Transport.Process)]
    public void CombinedSamplerInAModule_IsFoundThroughTheQuotedImport(Transport transport)
    {
        // slangc locates a combined sampler's halves in its own core module, never the author's
        // file, so the module is reached through the entry text's quoted import instead.
        const string emission = """
            #line 93 "core"
            Texture2D<float4 > ModComb_texture_0 : register(t6);

            #line 1188 "hlsl.meta.slang"
            SamplerState ModComb_sampler_0 : register(s0);

            #line 3 "<stdin>"
            float4 MainPS(float2 uv_0 : TEXCOORD0) : SV_TARGET
            {
                return ModComb_texture_0.Sample(ModComb_sampler_0, uv_0);
            }

            """;
        const string entry =
            "import \"C:/shaders/texmod.slang\" ; [ shader ( \"fragment\" ) ] float4 MainPS ( float2 uv : TEXCOORD0 ) : SV_Target { return ModComb . Sample ( uv ) ; } \n";
        const string source =
            "import \"C:/shaders/texmod.slang\";\n[shader(\"fragment\")]\nfloat4 MainPS(float2 uv : TEXCOORD0) : SV_Target { return ModComb.Sample(uv); }\n";
        var downstream = new CapturingCompiler();
        var slangc = new FakeSlangc(emission, entry,
            new Dictionary<string, (int, string, string)>
            {
                [ModulePath] = (0, "module texmod ; public Sampler2D ModComb : register ( t6 ) ; \n", ""),
            });

        var result = Create(transport, downstream, slangc).Compile(source, Options(PlatformTarget.DirectX));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.Captured.ShouldNotBeNull();
        fx.ShouldContain("Texture2D<float4 > ModComb_texture_0 : register(t6);", Case.Sensitive);
        fx.ShouldContain("SamplerState ModComb_sampler_0;", Case.Sensitive);
    }
}
