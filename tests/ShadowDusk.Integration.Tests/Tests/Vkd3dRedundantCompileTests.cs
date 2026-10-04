#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Issue #255: an effect whose techniques share entry points used to run vkd3d once per
/// pass that named one. MonoGame's stock <c>BasicEffect.fx</c> made 64 vkd3d calls for 30
/// distinct shaders (measured: about 530 ms of vkd3d time, now about half). The pipeline now
/// compiles each distinct request once; the output is pinned unchanged by the golden and
/// cross-host byte-identity suites, so these tests pin the call count (against the real
/// vkd3d) and the memo's premise that a repeat request gives the same bytes.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "FNA")]
public sealed class Vkd3dRedundantCompileTests
{
    private sealed class CountingVkd3d : IDxbcShaderCompiler
    {
        private readonly Vkd3dShaderCompiler _inner = new();
        public List<(string Entry, ShaderStage Stage, string? Profile)> Requests { get; } = [];

        /// <summary>The full request and the bytes (null on failure) vkd3d returned for it.</summary>
        public List<(D3DCompileRequest Request, byte[]? Bytes)> Calls { get; } = [];

        public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Compile(request, cancellationToken));

        public Result<PlatformBlob, ShaderError> Compile(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add((request.EntryPoint, request.Stage, request.ProfileOverride));
            var result = _inner.Compile(request, cancellationToken);
            Calls.Add((request, result.IsSuccess ? result.Value.Bytes.ToArray() : null));
            return result;
        }
    }

    /// <summary>
    /// MonoGame's stock effects, with the count of distinct shaders their techniques name
    /// (measured against the real vkd3d). SkinnedEffect on FNA is absent: it exceeds the
    /// vs_2_0 register file and fails loudly (SD0305, project_facts.md), so it never reaches
    /// a full compile.
    /// </summary>
    public static TheoryData<string, PlatformTarget, int> StockEffectCounts => new()
    {
        { "SkinnedEffect.fx", PlatformTarget.DirectX, 12 },
        { "EnvironmentMapEffect.fx", PlatformTarget.DirectX, 8 },
        { "EnvironmentMapEffect.fx", PlatformTarget.Fna, 8 },
        { "DualTextureEffect.fx", PlatformTarget.DirectX, 6 },
        { "DualTextureEffect.fx", PlatformTarget.Fna, 6 },
        { "AlphaTestEffect.fx", PlatformTarget.DirectX, 8 },
        { "AlphaTestEffect.fx", PlatformTarget.Fna, 8 },
        { "SpriteEffect.fx", PlatformTarget.DirectX, 2 },
        { "SpriteEffect.fx", PlatformTarget.Fna, 2 },
    };

    public static TheoryData<string, PlatformTarget> StockEffects => new()
    {
        { "BasicEffect.fx", PlatformTarget.DirectX },
        { "BasicEffect.fx", PlatformTarget.Fna },
        { "SkinnedEffect.fx", PlatformTarget.DirectX },
        { "SkinnedEffect.fx", PlatformTarget.Fna },
        { "EnvironmentMapEffect.fx", PlatformTarget.DirectX },
        { "EnvironmentMapEffect.fx", PlatformTarget.Fna },
        { "DualTextureEffect.fx", PlatformTarget.DirectX },
        { "DualTextureEffect.fx", PlatformTarget.Fna },
        { "AlphaTestEffect.fx", PlatformTarget.DirectX },
        { "AlphaTestEffect.fx", PlatformTarget.Fna },
        { "SpriteEffect.fx", PlatformTarget.DirectX },
        { "SpriteEffect.fx", PlatformTarget.Fna },
    };

    private static async Task<(CountingVkd3d Backend, Result<CompiledShader, ShaderError[]> Result)> CompileAsync(
        string fx, PlatformTarget target, bool bypassMemo = false)
    {
        string path = TestHelpers.FixturePath(fx);
        var counting = new CountingVkd3d();
        var result = await new EffectCompiler(dxbcCompilerFactory: () => counting).CompileAsync(
            await File.ReadAllTextAsync(path),
            new CompilerOptions { Target = target, SourceFileName = path, BypassDxbcMemo = bypassMemo });
        return (counting, result);
    }

    [FnaTheory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Fna)]
    public async Task BasicEffect_CompilesEachDistinctEntryPointOnce(PlatformTarget target)
    {
        var (counting, result) = await CompileAsync("BasicEffect.fx", target);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : null);
        counting.Requests.Count.ShouldBe(counting.Requests.Distinct().Count(),
            "every request reaching vkd3d must be a distinct one");
        // 32 techniques x (VS + PS) name 30 distinct shaders.
        counting.Requests.Count.ShouldBe(30);
    }

    [FnaTheory]
    [MemberData(nameof(StockEffectCounts))]
    public async Task StockEffect_CompilesEachDistinctEntryPointOnce(
        string fx, PlatformTarget target, int distinctShaders)
    {
        var (counting, result) = await CompileAsync(fx, target);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : null);
        counting.Requests.Count.ShouldBe(counting.Requests.Distinct().Count(),
            "every request reaching vkd3d must be a distinct one");
        counting.Requests.Count.ShouldBe(distinctShaders);
    }

    [FnaTheory]
    [MemberData(nameof(StockEffects))]
    public async Task EveryStockEffect_NeverSendsVkd3dTheSameRequestTwice(string fx, PlatformTarget target)
    {
        // Success is not required: SkinnedEffect on FNA fails loudly mid-compile (SD0305), and
        // the requests made up to that point must still be distinct. Compared on the FULL
        // request, not just (entry, stage, profile): a repeat differing in no field is the bug.
        var (counting, _) = await CompileAsync(fx, target);

        counting.Calls.ShouldNotBeEmpty();
        counting.Calls.Select(c => RequestKey(c.Request)).Distinct().Count()
            .ShouldBe(counting.Calls.Count, "the memo key must catch every repeat");
    }

    /// <summary>
    /// The memo's premise (issue #255): vkd3d is deterministic, so replaying a request would
    /// have produced the bytes the first call did. Re-run every distinct request fresh, and the
    /// whole compile twice; all must be byte-identical. This checks the property that makes the
    /// memo output-neutral; <see cref="MemoizedAndUnmemoizedCompiles_AreByteIdentical"/> compares
    /// the two pipeline runs themselves.
    /// </summary>
    [FnaTheory]
    [MemberData(nameof(StockEffects))]
    public async Task MemoizedOutput_EqualsFreshVkd3dBytes_AndIsRepeatable(string fx, PlatformTarget target)
    {
        var (first, firstResult) = await CompileAsync(fx, target);
        var (second, secondResult) = await CompileAsync(fx, target);

        var fresh = new Vkd3dShaderCompiler();
        foreach (var (request, bytes) in first.Calls)
        {
            var again = fresh.Compile(request);
            if (bytes is null)
            {
                again.IsFailure.ShouldBeTrue($"{request.EntryPoint}: failed once, must fail again");
                continue;
            }
            again.IsSuccess.ShouldBeTrue(again.IsFailure ? again.Error.Message : null);
            again.Value.Bytes.ToArray().ShouldBe(bytes, $"{request.EntryPoint}: a repeat must give the cached bytes");
        }

        firstResult.IsSuccess.ShouldBe(secondResult.IsSuccess);
        if (firstResult.IsSuccess)
            secondResult.Value.Data.ShouldBe(firstResult.Value.Data);
        second.Calls.Select(c => c.Bytes).ShouldBe(first.Calls.Select(c => c.Bytes));
    }

    /// <summary>
    /// Issue #358: the same effect compiled through the memo (the shipping path) and with it
    /// bypassed (the internal <c>CompilerOptions.BypassDxbcMemo</c> seam) must give the same
    /// container bytes, or the same errors where the compile fails. The call counts prove the
    /// bypass really reached vkd3d once per pass, so the two arms are not the memo twice.
    /// </summary>
    [FnaTheory]
    [MemberData(nameof(StockEffects))]
    public async Task MemoizedAndUnmemoizedCompiles_AreByteIdentical(string fx, PlatformTarget target)
    {
        var (memo, memoResult) = await CompileAsync(fx, target);
        var (direct, directResult) = await CompileAsync(fx, target, bypassMemo: true);

        direct.Calls.Count.ShouldBeGreaterThanOrEqualTo(memo.Calls.Count);
        memo.Calls.Count.ShouldBe(memo.Calls.Select(c => RequestKey(c.Request)).Distinct().Count(),
            "the memo arm must reach vkd3d once per distinct request");
        // Every request the memo arm made, the bypass arm made too (plus the repeats).
        direct.Calls.Select(c => RequestKey(c.Request)).Distinct()
            .ShouldBe(memo.Calls.Select(c => RequestKey(c.Request)), ignoreOrder: false);

        directResult.IsSuccess.ShouldBe(memoResult.IsSuccess,
            memoResult.IsFailure ? memoResult.Error[0].Message : directResult.IsFailure ? directResult.Error[0].Message : null);
        if (memoResult.IsSuccess)
        {
            directResult.Value.Data.ShouldBe(memoResult.Value.Data, $"{fx} on {target}: memo and no-memo bytes differ");
            directResult.Value.Warnings.Select(w => (w.Code, w.Message, w.File, w.Line, w.Column))
                .ShouldBe(memoResult.Value.Warnings.Select(w => (w.Code, w.Message, w.File, w.Line, w.Column)));
        }
        else
        {
            directResult.Error.Select(e => (e.Code, e.Message, e.File, e.Line, e.Column))
                .ShouldBe(memoResult.Error.Select(e => (e.Code, e.Message, e.File, e.Line, e.Column)));
        }
    }

    [FnaFact]
    public async Task BasicEffect_WithTheMemoBypassed_ReachesVkd3dOncePerPassShader()
    {
        // The pre-#255 count: 32 techniques x (VS + PS) = 64 calls for 30 distinct shaders.
        // Proves the seam really removes the memo rather than the arms both being memoized.
        var (direct, result) = await CompileAsync("BasicEffect.fx", PlatformTarget.DirectX, bypassMemo: true);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : null);
        direct.Calls.Count.ShouldBe(64);
        direct.Calls.Select(c => RequestKey(c.Request)).Distinct().Count().ShouldBe(30);
    }

    private static string RequestKey(D3DCompileRequest r) =>
        string.Join('\u001f', r.SourceFileName, r.EntryPoint, r.Stage, r.ProfileOverride,
            r.EmbedDebugInfo, r.AllowWarnings, r.HlslSource);
}
