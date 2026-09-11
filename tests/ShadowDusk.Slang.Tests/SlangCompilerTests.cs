#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using ShadowDusk.Integration.Tests;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Integration tests for <see cref="SlangCompiler"/> — the real-slangc compile route (Phase
/// 66 A3). Every test here spawns the real, restored <c>tools/slang/win-x64/slangc.exe</c>
/// (and, unless a stub downstream compiler is injected, the real faithful pipeline behind
/// it), so the whole class is tagged Category=Integration, matching
/// <c>ShadowDusk.Compiler.Tests.EffectCompilerTests</c>'s own convention.
///
/// <para>Requires <c>tools/restore.ps1</c> / <c>restore.sh</c> to have restored
/// <c>tools/slang/win-x64/slangc.exe</c> first — see <c>SlangToolPath</c>. Windows-x64 only
/// today (Phase 66 A2), matching the package's platform support.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangCompilerTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SlangCorpusDir = Path.Combine(RepoRoot, "tests", "fixtures", "shaders", "slang");
    private static readonly string GumProbeDir = Path.Combine(
        RepoRoot, "plan", "PHASE-65-appendix", "slang-probe", "shaders");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }

    private static string FormatErrors(ShaderError[] errors) => string.Join("; ", errors.Select(e => e.FxcFormattedMessage));

    // ---------------------------------------------------------------------------
    // Corpus sweep — the shipped 17-shader .slang fixture corpus, plus the Phase 65
    // Gum-shaped and generics-probe shaders, on OpenGL and DirectX.
    //
    // Phase 66 A3 left 3 OpenGL failures (Desaturate/ScrollUv/WaveVertex, every corpus
    // shader with a float4x4 cbuffer member): SPIRV-Cross reflected a 'layout(row_major)'
    // qualifier on the mat4 member that MonoGameGlslRewriter.UniformMember's regex does not
    // accept. A4 root-caused this to slangc's own unconditional '#pragma
    // pack_matrix(column_major)' silently overriding DxcFlagBuilder's OpenGL '-Zpr'
    // (row-major) convention, and fixed it in SlangCompiler.StripMatrixPackingPragma — see
    // that method's comment for the full mechanism. All 21 corpus shaders now pass on both
    // targets; there is no longer a known-failure carve-out here.
    // ---------------------------------------------------------------------------

    public static IEnumerable<object[]> CorpusFiles()
    {
        foreach (string f in Directory.GetFiles(SlangCorpusDir, "*.slang").OrderBy(x => x, StringComparer.Ordinal))
            yield return [Path.GetFileName(f), f];

        foreach (string name in new[] { "GumGrayscale.slang", "GumTint.slang", "GumBlur.slang", "GenericsProbe.slang" })
            yield return [name, Path.Combine(GumProbeDir, name)];
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task Corpus_CompilesThroughTheRealPipeline_DirectX(string name, string path)
    {
        string source = await File.ReadAllTextAsync(path);
        var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = name };

        var result = await new SlangCompiler().CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? $"{name}: {FormatErrors(result.Error)}" : "");
        result.Value.Data.ShouldNotBeEmpty();
        AssertLooksLikeMgfx(result.Value.Data);
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public async Task Corpus_CompilesThroughTheRealPipeline_OpenGL(string name, string path)
    {
        string source = await File.ReadAllTextAsync(path);
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = name };

        var result = await new SlangCompiler().CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? $"{name}: {FormatErrors(result.Error)}" : "");
        result.Value.Data.ShouldNotBeEmpty();
        AssertLooksLikeMgfx(result.Value.Data);
    }

    /// <summary>
    /// Rung 2 (structural well-formedness): the compiled bytes parse as a real MGFX
    /// container. Minimal header parse mirrors ShadowDusk.Compiler.Tests.EffectCompilerTests'
    /// ReadConstantBufferCount helper: "MGFX"(4) + version(1) + profile(1) + effectKey(4).
    /// </summary>
    private static void AssertLooksLikeMgfx(byte[] mgfxBytes)
    {
        mgfxBytes.Length.ShouldBeGreaterThanOrEqualTo(14);
        System.Text.Encoding.ASCII.GetString(mgfxBytes, 0, 4).ShouldBe("MGFX");
    }

    // ---------------------------------------------------------------------------
    // The generics probe: real Slang-only syntax (a generic function over an `interface`
    // conformance) the subset frontend (SD0600/the un-caught 'interface' gap, Phase 65 §5)
    // cannot compile at all. Proving THIS compiles end to end is A3's actual point.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GenericsProbe_GenuinelySlangOnlySyntax_CompilesThroughRealSlangc()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(GumProbeDir, "GenericsProbe.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "GenericsProbe.slang" };

        var result = await new SlangCompiler().CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
    }

    // ---------------------------------------------------------------------------
    // VS+PS merge path, observed through an injected stub downstream compiler: proves the
    // real slangc invocations + SlangHlslMerger dedup + technique synthesis wiring produce
    // the expected shape, without paying for two full native pipeline compiles per assertion.
    // ---------------------------------------------------------------------------

    private sealed class CapturingCompiler : IShaderCompiler
    {
        public string? CapturedHlslSource;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedHlslSource = hlslSource;
            return Task.FromResult(Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58])));
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedHlslSource = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }

    [Fact]
    public async Task WaveVertex_VsPlusPs_MergesBothEntries_AndSynthesizesBothTechniquePasses()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(SlangCorpusDir, "WaveVertex.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "WaveVertex.slang" };
        var capture = new CapturingCompiler();

        var result = await new SlangCompiler(capture).CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        string fx = capture.CapturedHlslSource.ShouldNotBeNull();

        fx.ShouldContain("technique SlangEffect", Case.Sensitive);
        fx.ShouldContain("VertexShader = compile VS_SHADERMODEL MainVS();", Case.Sensitive);
        fx.ShouldContain("PixelShader = compile PS_SHADERMODEL MainPS();", Case.Sensitive);
        fx.ShouldContain("#if SM4", Case.Sensitive);

        // The dedup did its job: the VS+PS pair's shared VSOutput struct (real slangc
        // mangles it, so the exact suffix isn't pinned here) appears exactly once.
        int structOccurrences = CountOccurrences(fx, "struct VSOutput");
        structOccurrences.ShouldBe(1, $"expected the shared VSOutput struct once, found {structOccurrences} in:\n{fx}");
    }

    [Fact]
    public async Task PixelOnlyShader_SynthesizesAPixelOnlyPass_NoVertexShaderLine()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(SlangCorpusDir, "Checkerboard.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.DirectX, SourceFileName = "Checkerboard.slang" };
        var capture = new CapturingCompiler();

        var result = await new SlangCompiler(capture).CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        string fx = capture.CapturedHlslSource.ShouldNotBeNull();

        fx.ShouldContain("PixelShader = compile", Case.Sensitive);
        fx.ShouldNotContain("VertexShader = compile", Case.Sensitive);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---------------------------------------------------------------------------
    // Diagnostics: a deliberately broken Slang source must surface slangc's OWN error,
    // verbatim, with a real slangc code — never a generic ShadowDusk message.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task InvalidSlangSource_SurfacesSlangcsOwnDiagnostic_Verbatim()
    {
        const string broken = """
            [shader("fragment")]
            float4 MainPS() : SV_Target
            {
                return this_identifier_does_not_exist(1, 2, 3);
            }
            """;
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "broken.slang" };

        var result = await new SlangCompiler().CompileAsync(broken, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.File.ShouldBe("broken.slang");
        error.Message.ShouldContain("this_identifier_does_not_exist", Case.Sensitive);
        error.Line.ShouldBeGreaterThan(0);
    }

    // ---------------------------------------------------------------------------
    // Entry-point policy is SlangEntryScanner's, reused unchanged: these two never even
    // reach slangc (the scan fails first), but stay in this Integration-tagged class for
    // simplicity alongside the rest of SlangCompiler's surface.
    // ---------------------------------------------------------------------------

    // Phase 66 A5, Band 2 part A: a real compute entry point (genuine [numthreads]/
    // groupshared/SV_DispatchThreadID compute syntax, not just an attribute-only stub) must
    // reject with SD0602 on EVERY target — the rejection is entry-stage policy
    // (SlangEntryScanner.Scan), decided before any per-target compile step runs, so it can
    // never depend on which target was asked for.
    private const string ComputeEntrySource = """
        RWStructuredBuffer<float> Particles;

        [shader("compute")]
        [numthreads(64, 1, 1)]
        void Simulate(uint3 id : SV_DispatchThreadID)
        {
            Particles[id.x] += 1.0;
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.DirectX12)]
    [InlineData(PlatformTarget.Vulkan)]
    [InlineData(PlatformTarget.Fna)]
    public async Task ComputeEntryPoint_RejectedLoudly_BeforeInvokingSlangc_OnEveryTarget(
        PlatformTarget target)
    {
        var options = new CompilerOptions { Target = target, SourceFileName = "compute.slang" };

        var result = await new SlangCompiler().CompileAsync(ComputeEntrySource, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0602");
        error.Message.ShouldContain("Simulate", Case.Sensitive);
        error.Message.ShouldContain("compute", Case.Sensitive);
    }

    [Fact]
    public async Task ComputeEntryPoint_RejectedBeforeSpawningSlangc_NeverReachesADownstreamError()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "compute.slang" };

        var result = await new SlangCompiler(capture).CompileAsync(ComputeEntrySource, options);

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0602");
        capture.CapturedHlslSource.ShouldBeNull();
    }

    [Fact]
    public async Task NoEntryPoints_RejectedLoudly_BeforeInvokingSlangc()
    {
        const string noEntries = "float4 Helper() { return 0; }";
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "lib.slang" };

        var result = await new SlangCompiler().CompileAsync(noEntries, options);

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0603");
    }

    // ---------------------------------------------------------------------------
    // Determinism — the same claim ShadowDusk.Compiler.Tests.EffectCompilerTests pins for
    // EffectCompiler itself: same source/target/version in, same bytes out.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Compile_Deterministic_SameBytesOnRepeat()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(SlangCorpusDir, "Sepia.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "Sepia.slang" };
        var compiler = new SlangCompiler();

        var first = await compiler.CompileAsync(source, options);
        var second = await compiler.CompileAsync(source, options);

        first.IsSuccess.ShouldBeTrue(first.IsFailure ? FormatErrors(first.Error) : "");
        second.IsSuccess.ShouldBeTrue(second.IsFailure ? FormatErrors(second.Error) : "");
        second.Value.Data.ShouldBe(first.Value.Data);
    }

    // ---------------------------------------------------------------------------
    // User defines forward to slangc's own preprocessor (-D<name>=<value>), the same
    // CompilerOptions.Defines slot every other frontend honors.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task UserDefines_ForwardToSlangcsPreprocessor()
    {
        const string source = """
            [shader("fragment")]
            float4 MainPS() : SV_Target
            {
            #if TINT_RED
                return float4(1, 0, 0, 1);
            #else
                return float4(0, 1, 0, 1);
            #endif
            }
            """;
        var options = new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
            SourceFileName = "tint.slang",
            Defines = [new UserDefine("TINT_RED", "1")],
        };
        var capture = new CapturingCompiler();

        var result = await new SlangCompiler(capture).CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        string fx = capture.CapturedHlslSource.ShouldNotBeNull();
        // slangc reformats numeric literals (adds explicit '.0f'/'f' suffixes) — assert on
        // the values, not the author's original literal spelling.
        fx.ShouldContain("float4(1.0f, 0.0f, 0.0f, 1.0f)", Case.Sensitive);
        fx.ShouldNotContain("float4(0.0f, 1.0f, 0.0f, 1.0f)", Case.Sensitive);
    }

    // ---------------------------------------------------------------------------
    // Phase 66 A4, Problem 1 — the mangling fix ('-no-mangle'). A parameter name written
    // in .slang source must round-trip, UNMANGLED, all the way through the real pipeline
    // into the compiled effect's own reflected parameter table (Effect.Parameters['Name']
    // is the exact consumer-visible surface this matters for) — not just into slangc's
    // intermediate HLSL text.
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.OpenGL)]
    public async Task ParameterNames_RoundTripUnmangled_ThroughTheCompiledEffectsParameterTable(
        PlatformTarget target)
    {
        string source = await File.ReadAllTextAsync(
            Path.Combine(GumProbeDir, "GumTint.slang"));
        var options = new CompilerOptions { Target = target, SourceFileName = "GumTint.slang" };

        var result = await new SlangCompiler().CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        var mgfx = MgfxBlobReader.Parse(result.Value.Data);

        // GumTint.slang declares 'float3 TintColor' and 'float TintAmount' in its cbuffer,
        // plus 'Texture2D SpriteTexture'/'SamplerState SpriteTextureSampler' — the author's
        // exact spellings, none of slangc's own '_0'/'_1' mangling suffix.
        mgfx.ParameterNames.ShouldContain("TintColor");
        mgfx.ParameterNames.ShouldContain("TintAmount");
        foreach (string name in mgfx.ParameterNames)
            name.ShouldNotMatch(@"_[0-9]+$", $"parameter '{name}' still carries a slangc mangling suffix");
    }

    // ---------------------------------------------------------------------------
    // Phase 66 A4, Problem 2 — the OpenGL row_major/float4x4 fix. Direct coverage (beyond
    // the corpus sweep above) for the exact repro shape: a cbuffer member declared
    // 'float4x4', compiled for OpenGL. Asserts BOTH that the real pipeline compiles it
    // successfully now, and that the HLSL text handed to the downstream compiler no longer
    // carries slangc's own '#pragma pack_matrix(column_major)' (the root cause).
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Float4x4CbufferMember_CompilesOnOpenGL_WithMatrixPackingPragmaStripped()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(SlangCorpusDir, "WaveVertex.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "WaveVertex.slang" };
        var capture = new CapturingCompiler();

        var result = await new SlangCompiler(capture).CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        string fx = capture.CapturedHlslSource.ShouldNotBeNull();
        fx.ShouldNotContain("pack_matrix", Case.Sensitive);
        fx.ShouldContain("float4x4 WorldViewProjection", Case.Sensitive);
    }

    [Fact]
    public async Task Float4x4CbufferMember_CompilesOnOpenGL_ThroughTheRealPipeline()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(SlangCorpusDir, "WaveVertex.slang"));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "WaveVertex.slang" };

        var result = await new SlangCompiler().CompileAsync(source, options);

        result.IsSuccess.ShouldBeTrue(
            result.IsFailure ? $"WaveVertex.slang: {FormatErrors(result.Error)}" : "");
        AssertLooksLikeMgfx(result.Value.Data);
    }

    // ---------------------------------------------------------------------------
    // Phase 66 A5, Band 2 part B (OQ2) — an SM6-only wave intrinsic rejects with SD0624 on the
    // three targets architecturally capped below SM6, and (measured, Phase 66 A5: the ONE target
    // that reaches it end to end through ShadowDusk's real pipeline today) still compiles on
    // DirectX12. Vulkan is deliberately NOT asserted to succeed here — see
    // SlangSm6ConstructGuard.IsArchitecturallyBelowSm6's own doc comment for the separate,
    // pre-existing DxcFlagBuilder '-fspv-target-env' gap this stage found but left unfixed
    // (out of scope: it is a general Vulkan/DXC pipeline flag, not Slang-specific).
    // ---------------------------------------------------------------------------

    private const string WaveIntrinsicSource = """
        struct VSOutput
        {
            float4 Position : SV_Position;
            float2 UV : TEXCOORD0;
        };

        [shader("fragment")]
        float4 MainPS(VSOutput input) : SV_Target
        {
            float v = WaveActiveSum(input.UV.x);
            return float4(v, v, v, 1.0);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Fna)]
    public async Task WaveIntrinsic_RejectedWithSD0624_OnTargetsArchitecturallyBelowSm6(
        PlatformTarget target)
    {
        var options = new CompilerOptions { Target = target, SourceFileName = "wave.slang" };

        var result = await new SlangCompiler().CompileAsync(WaveIntrinsicSource, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0624");
        error.Message.ShouldContain("WaveActiveSum", Case.Sensitive);
        error.Message.ShouldContain(target.ToString(), Case.Sensitive);
        error.Line.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task WaveIntrinsic_RejectedBeforeSpawningSlangc_NeverReachesADownstreamError()
    {
        // The guard runs on the raw Slang source before slangc is ever invoked (SD0624's own
        // point: one consistent diagnostic instead of each backend's own confusing wording) —
        // proven here via a stub downstream compiler that would fail the test outright if
        // SlangCompiler somehow got as far as assembling/handing off an .fx body.
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "wave.slang" };

        var result = await new SlangCompiler(capture).CompileAsync(WaveIntrinsicSource, options);

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0624");
        capture.CapturedHlslSource.ShouldBeNull();
    }

    [Fact]
    public async Task WaveIntrinsic_CompilesOnDirectX12_TheOneTargetThatReachesItToday()
    {
        var options = new CompilerOptions { Target = PlatformTarget.DirectX12, SourceFileName = "wave.slang" };

        var result = await new SlangCompiler().CompileAsync(WaveIntrinsicSource, options);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? FormatErrors(result.Error) : "");
        AssertLooksLikeMgfx(result.Value.Data);
    }
}
