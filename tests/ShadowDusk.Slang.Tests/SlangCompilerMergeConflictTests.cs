#nullable enable

using ShadowDusk.Core;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Drives <see cref="SlangCompiler"/> end to end with a FAKE slangc (issue #258): the slangc
/// locator, native-cache preparation and process spawn are all replaced, so canned per-entry
/// HLSL reaches the real merge step and the real <c>SD0625</c> rejection. No valid Slang
/// source reproduces <c>SD0625</c> through real slangc (cbuffer and resource names keep the
/// author's spelling under <c>-no-mangle</c>, so two entries cannot disagree on one), which is
/// why <see cref="SlangHlslMergerCollisionTests"/> alone used to cover it. Pure: no disk, no
/// process.
/// </summary>
public sealed class SlangCompilerMergeConflictTests
{
    private const string FakeSlangcPath = "fake-tools/slangc";

    // Two real [shader] entries, so SlangEntryScanner reports a vertex and a fragment entry
    // and SlangCompiler invokes the (fake) slangc once per entry.
    private const string TwoEntrySource = """
        [shader("vertex")]
        float4 MainVS(float4 p : POSITION) : SV_Position { return p; }

        [shader("fragment")]
        float4 MainPS() : SV_Target { return 1.0; }
        """;

    private const string Prelude = """
        #pragma pack_matrix(column_major)
        #ifdef SLANG_HLSL_ENABLE_NVAPI
        #include "nvHLSLExtns.h"
        #endif

        """;

    private const string VsWithParamsA = Prelude + """
        #line 3
        cbuffer Params : register(b0)
        {
            float A;
        }

        #line 9
        float4 MainVS(float4 p_0 : POSITION) : SV_Position
        {
            return p_0 * A;
        }

        """;

    private const string PsWithParamsB = Prelude + """
        #line 3
        cbuffer Params : register(b0)
        {
            float B;
        }

        #line 12
        float4 MainPS() : SV_TARGET
        {
            return B;
        }

        """;

    private const string PsWithParamsA = Prelude + """
        #line 3
        cbuffer Params : register(b0)
        {
            float A;
        }

        #line 12
        float4 MainPS() : SV_TARGET
        {
            return A;
        }

        """;

    private sealed class RecordingCompiler : IShaderCompiler
    {
        public string? CapturedFx;

        public Task<Result<CompiledShader, ShaderError[]>> CompileAsync(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(Compile(hlslSource, options, cancellationToken));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Result<CompiledShader, ShaderError[]> Compile(
            string hlslSource, CompilerOptions options, CancellationToken cancellationToken = default)
        {
            CapturedFx = hlslSource;
            return Result<CompiledShader, ShaderError[]>.Ok(
                new CompiledShader(options.Target, [0x4D, 0x47, 0x46, 0x58]));
        }
    }

    private sealed class FakeSlangc
    {
        private readonly IReadOnlyDictionary<string, string> _hlslByEntry;
        public List<(string Entry, string Stage)> Calls { get; } = [];

        public FakeSlangc(IReadOnlyDictionary<string, string> hlslByEntry) => _hlslByEntry = hlslByEntry;

        public (int ExitCode, string Stdout, string Stderr) Run(
            string slangcPath, string workingDirectory, string slangSource, IReadOnlyList<string> arguments)
        {
            slangcPath.ShouldBe(FakeSlangcPath);
            // The per-entry compile shape of SlangcArguments.Build: '... -entry <name> -stage <stage> -- -'.
            int entry = arguments.ToList().IndexOf("-entry");
            entry.ShouldBeGreaterThanOrEqualTo(0, "the source writes no register, so only entry compiles may run");
            string entryName = arguments[entry + 1];
            string stage = arguments[entry + 3];
            Calls.Add((entryName, stage));
            return (0, _hlslByEntry[entryName], "");
        }
    }

    private static SlangCompiler Create(RecordingCompiler downstream, FakeSlangc slangc) =>
        new(downstream,
            () => new SlangCompiler.SlangcLocation(null, FakeSlangcPath),
            prepareSlangc: path => path,
            runSlangc: slangc.Run);

    private static CompilerOptions Options(PlatformTarget target) =>
        new() { Target = target, SourceFileName = "Conflict.slang" };

    [Theory]
    [InlineData(PlatformTarget.OpenGL)]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Vulkan)]
    public void CbufferDeclaredDifferentlyPerEntry_FailsWithSD0625_AndNeverReachesDownstream(PlatformTarget target)
    {
        var downstream = new RecordingCompiler();
        var slangc = new FakeSlangc(new Dictionary<string, string>
        {
            ["MainVS"] = VsWithParamsA,
            ["MainPS"] = PsWithParamsB,
        });

        Result<CompiledShader, ShaderError[]> result = Create(downstream, slangc).Compile(TwoEntrySource, Options(target));

        result.IsFailure.ShouldBeTrue();
        result.Error.Length.ShouldBe(1);
        ShaderError error = result.Error[0];
        error.Code.ShouldBe("SD0625");
        error.File.ShouldBe("Conflict.slang");
        (error.Line, error.Column).ShouldBe((0, 0));
        error.Message.ShouldContain("'Params'", Case.Sensitive);
        downstream.CapturedFx.ShouldBeNull();
        slangc.Calls.ShouldBe([("MainVS", "vertex"), ("MainPS", "fragment")]);
    }

    [Fact]
    public void IdenticalCbufferPerEntry_MergesOnce_AndTheAssembledFxReachesDownstream()
    {
        // The control: the same seam with agreeing declarations must compile, so the SD0625
        // test above cannot pass merely because the seam itself fails.
        var downstream = new RecordingCompiler();
        var slangc = new FakeSlangc(new Dictionary<string, string>
        {
            ["MainVS"] = VsWithParamsA,
            ["MainPS"] = PsWithParamsA,
        });

        Result<CompiledShader, ShaderError[]> result =
            Create(downstream, slangc).Compile(TwoEntrySource, Options(PlatformTarget.OpenGL));

        result.IsSuccess.ShouldBeTrue();
        string fx = downstream.CapturedFx.ShouldNotBeNull();
        fx.Split("cbuffer Params").Length.ShouldBe(2);
        fx.ShouldContain("VertexShader = compile VS_SHADERMODEL MainVS();", Case.Sensitive);
        fx.ShouldContain("PixelShader = compile PS_SHADERMODEL MainPS();", Case.Sensitive);
        fx.ShouldNotContain("#pragma pack_matrix", Case.Sensitive);
    }
}
