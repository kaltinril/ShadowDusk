#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// <see cref="SlangCompiler"/>'s rejections that depend only on the source and the target
/// (entry-stage policy <c>SD0602</c>/<c>SD0603</c>, the SM6 intrinsic guard <c>SD0624</c>)
/// and its host/native failure reporting (<c>SD0620</c>/<c>SD0621</c>/<c>SD0623</c>).
///
/// <para>These used to sit in the Integration-tagged <see cref="SlangCompilerTests"/> and ran
/// AFTER the host/native check, so on a host without a usable slangc they returned
/// <c>SD0620</c> instead of the code under test. The host check now runs after them, and
/// these tests prove it by injecting a slangc locator that FAILS the test if it is ever
/// reached. No native process is spawned, so they run in the unit lane on every OS.</para>
/// </summary>
public sealed class SlangCompilerHostIndependentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sd-slang-hostindep-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static SlangCompiler.SlangcLocation MustNotBeReached() =>
        throw new InvalidOperationException(
            "the slangc host/native lookup ran before a host-independent rejection");

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

    private static SlangCompiler PreSpawnOnly(CapturingCompiler capture) => new(capture, MustNotBeReached);

    // Phase 66 A5, Band 2 part A: a real compute entry point (genuine [numthreads]/
    // SV_DispatchThreadID compute syntax, not just an attribute-only stub) must reject with
    // SD0602 on EVERY target: entry-stage policy (SlangEntryScanner.Scan) cannot depend on
    // the target, nor on the host.
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
    public void ComputeEntryPoint_RejectedWithSD0602_OnEveryTarget_BeforeTheHostLookup(PlatformTarget target)
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = target, SourceFileName = "compute.slang" };

        var result = PreSpawnOnly(capture).Compile(ComputeEntrySource, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0602");
        error.Message.ShouldContain("Simulate", Case.Sensitive);
        error.Message.ShouldContain("compute", Case.Sensitive);
        capture.CapturedHlslSource.ShouldBeNull();
    }

    [Fact]
    public void NoEntryPoints_RejectedWithSD0603_BeforeTheHostLookup()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "lib.slang" };

        var result = PreSpawnOnly(capture).Compile("float4 Helper() { return 0; }", options);

        result.IsFailure.ShouldBeTrue();
        result.Error.Single().Code.ShouldBe("SD0603");
        capture.CapturedHlslSource.ShouldBeNull();
    }

    private const string WaveIntrinsicSource = """
        struct PsInput
        {
            float4 Position : SV_Position;
            float2 UV       : TEXCOORD0;
        };

        [shader("fragment")]
        float4 MainPS(PsInput input) : SV_Target
        {
            float v = WaveActiveSum(input.UV.x);
            return float4(v, v, v, 1.0);
        }
        """;

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Fna)]
    public void WaveIntrinsic_RejectedWithSD0624_BelowSm6_BeforeTheHostLookup(PlatformTarget target)
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = target, SourceFileName = "wave.slang" };

        var result = PreSpawnOnly(capture).Compile(WaveIntrinsicSource, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0624");
        error.Message.ShouldContain("WaveActiveSum", Case.Sensitive);
        error.Message.ShouldContain(target.ToString(), Case.Sensitive);
        // The construct's own line in the source above (line 10, 1-based).
        error.File.ShouldBe("wave.slang");
        error.Line.ShouldBe(10);
        capture.CapturedHlslSource.ShouldBeNull();
    }

    // Issue #229: Vulkan can represent SM6, but MonoGame's DesktopVK cannot run wave/quad ops
    // (Vulkan 1.0 instance, no subgroup support). Same code and message as the .fx route.
    [Fact]
    public void WaveIntrinsic_RejectedWithSD0218_OnVulkan_BeforeTheHostLookup()
    {
        var capture = new CapturingCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.Vulkan, SourceFileName = "wave.slang" };

        var result = PreSpawnOnly(capture).Compile(WaveIntrinsicSource, options);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error.Single();
        error.Code.ShouldBe("SD0218");
        error.Message.ShouldContain("'WaveActiveSum'", Case.Sensitive);
        error.Message.ShouldContain("Vulkan 1.0", Case.Sensitive);
        error.Message.ShouldContain("no subgroup support", Case.Sensitive);
        error.File.ShouldBe("wave.slang");
        error.Line.ShouldBe(10);
        capture.CapturedHlslSource.ShouldBeNull();
    }

    // ------------------------------------------------ host/native failure reporting

    private const string ValidPixelShader = """
        [shader("fragment")]
        float4 MainPS() : SV_Target { return float4(1, 0, 0, 1); }
        """;

    [Fact]
    public void UnsupportedHost_SD0620_CarriesTheHostReasonVerbatim()
    {
        var compiler = new SlangCompiler(new CapturingCompiler(), () => new("REASON-FROM-HOST.", null));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "p.slang" };

        var result = compiler.Compile(ValidPixelShader, options);

        var error = result.Error.Single();
        error.Code.ShouldBe("SD0620");
        error.File.ShouldBe("p.slang");
        error.Message.ShouldStartWith("REASON-FROM-HOST.", Case.Sensitive);
    }

    [Fact]
    public void NativeNotFound_SD0621()
    {
        var compiler = new SlangCompiler(new CapturingCompiler(), () => new(null, null));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "p.slang" };

        var result = compiler.Compile(ValidPixelShader, options);

        var error = result.Error.Single();
        error.Code.ShouldBe("SD0621");
        error.Message.ShouldContain("runtimes/", Case.Sensitive);
    }

    [Fact]
    public void PartialNative_MissingCompilerLibrary_SD0623_NamesTheLibrary()
    {
        string rid = SlangToolPath.CurrentRid
            ?? throw new InvalidOperationException("test host is not a bundled slangc RID");
        Directory.CreateDirectory(_root);
        string slangc = Path.Combine(_root, SlangToolPath.ExecutableFileName(rid));
        File.WriteAllBytes(slangc, [1]);   // the compiler library deliberately absent

        var compiler = new SlangCompiler(new CapturingCompiler(), () => new(null, slangc));
        var options = new CompilerOptions { Target = PlatformTarget.OpenGL, SourceFileName = "p.slang" };

        var result = compiler.Compile(ValidPixelShader, options);

        var error = result.Error.Single();
        error.Code.ShouldBe("SD0623");
        error.Message.ShouldContain(SlangToolPath.CompilerLibraryFileName(rid), Case.Sensitive);
    }
}
