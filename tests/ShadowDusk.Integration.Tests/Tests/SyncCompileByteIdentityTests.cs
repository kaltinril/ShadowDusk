#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 42 (issue #28) acceptance gate: the synchronous <see cref="EffectCompiler.Compile"/>
/// must produce <b>byte-identical</b> output to <see cref="EffectCompiler.CompileAsync"/> for
/// the full fixture corpus, per target (OpenGL, DirectX, FNA).
///
/// <para>Identity is guaranteed by construction — both entry points run the ONE synchronous
/// <c>CompilationPipeline.Run</c> core (the issue's "no second pipeline" doctrine; the async
/// surface is a thin <c>Task.Run</c> shell) — but the issue's acceptance criteria say to
/// assert it anyway, so a future refactor that forks the implementations (and could silently
/// drift) trips this gate immediately.</para>
///
/// <para>The corpus and normalization mirror <see cref="CrossHostByteIdentityTests"/> (the
/// same fixtures, LF-normalized source, fixture-relative <c>SourceFileName</c>, vkd3d DXBC
/// backend for DirectX so the rows run on every OS). The sync arm deliberately runs on the
/// xunit worker thread with NO task in sight — proving the path truly is synchronous, not
/// sync-over-async.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SyncCompileByteIdentityTests
{
    private static readonly TimeSpan TargetTimeout = TimeSpan.FromSeconds(240);

    // Same corpus as CrossHostByteIdentityTests — keep in sync.
    private static readonly string[] CoreMgfxFixtures =
    [
        "Minimal.fx",
        "textured.fx",
        "cbuffer.fx",
        "multipass.fx",
        "multitechnique.fx",
        "render-states.fx",
        "annotations.fx",
        "platform-macros.fx",
        "basiceffect-mini.fx",
    ];

    private static readonly string[] Sm3Fixtures =
    [
        "Grayscale.fx",
        "Invert.fx",
        "Sepia.fx",
        "Saturate.fx",
        "Pixelated.fx",
        "Scanlines.fx",
        "Fading.fx",
        "Dots.fx",
        "Dissolve.fx",
        "TintShader.fx",
        "BasicShader.fx",
        "BlendShader.fx",
        "ClipShader.fx",
        "ClipShaderNew.fx",
        "ClipShaderSpriteTarget.fx",
        "MultiTexture.fx",
        "MultiTextureOverlay.fx",
        "SimpleLightShader.fx",
        "SpriteAlphaTest.fx",
        "Teleport.fx",
        "PolygonLight.fx",
        "VertexAndPixel.fx",
        "VsTransformColorTexture.fx",
        "FnaMultiPassStates.fx",
        "examples/ExBareSamplerTex2D.fx",
        "examples/ExSamplerStateUniform.fx",
        "examples/ExDualTexture.fx",
        "examples/ExLegacyTextureDiscard.fx",
    ];

    private static IEnumerable<string> OpenGLCorpus  => CoreMgfxFixtures.Concat(Sm3Fixtures);
    private static IEnumerable<string> DirectXCorpus => CoreMgfxFixtures.Concat(Sm3Fixtures);
    private static IEnumerable<string> FnaCorpus     => Sm3Fixtures;

    [DxcFact]
    public async Task OpenGL_SyncCompile_ByteIdentical_To_CompileAsync()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertSyncEqualsAsyncAsync(OpenGLCorpus, PlatformTarget.OpenGL, cts.Token);
    }

    [FnaFact] // vkd3d-shader availability gate — the DirectX rows pin the cross-platform backend
    public async Task DirectX_Vkd3d_SyncCompile_ByteIdentical_To_CompileAsync()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertSyncEqualsAsyncAsync(DirectXCorpus, PlatformTarget.DirectX, cts.Token);
    }

    [FnaFact]
    public async Task Fna_SyncCompile_ByteIdentical_To_CompileAsync()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        await AssertSyncEqualsAsyncAsync(FnaCorpus, PlatformTarget.Fna, cts.Token);
    }

    /// <summary>
    /// The d3dcompiler_47 oracle backend (opt-in via DxbcBackend.D3DCompiler; the
    /// library default is vkd3d) must serve the sync entry identically too — the
    /// "clean scope" of issue #28 covers ALL backends, not just the cross-platform set.
    /// Windows-only by nature (SD0210 elsewhere), so a single representative fixture
    /// suffices; the full-corpus DX assertion above runs the vkd3d backend on every OS.
    /// </summary>
    [WindowsFact]
    public async Task DirectX_Oracle_SyncCompile_ByteIdentical_To_CompileAsync()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        var compiler = new EffectCompiler();
        var options = BuildOptions("Minimal.fx", PlatformTarget.DirectX, DxbcBackend.D3DCompiler);
        string source = await ReadFixtureAsync("Minimal.fx", cts.Token);

        var syncResult  = compiler.Compile(source, options, cts.Token);
        var asyncResult = await compiler.CompileAsync(source, options, cts.Token);

        AssertSuccess(syncResult, "Minimal.fx", "sync/oracle");
        AssertSuccess(asyncResult, "Minimal.fx", "async/oracle");
        syncResult.Value.Data.Should().Equal(asyncResult.Value.Data);
    }

    /// <summary>
    /// The desktop <see cref="EffectCompiler.InitializeAsync"/> contract: completes (it
    /// is a documented no-op — natives load on first use), is idempotent/safe to await
    /// repeatedly, and a subsequent synchronous <see cref="EffectCompiler.Compile"/>
    /// works — the exact consumer pattern issue #28 prescribes.
    /// </summary>
    [DxcFact]
    public async Task InitializeAsync_Then_SyncCompile_Works()
    {
        using var cts = new CancellationTokenSource(TargetTimeout);
        var compiler = new EffectCompiler();

        await compiler.InitializeAsync(cts.Token);
        await compiler.InitializeAsync(cts.Token); // idempotent — safe to await repeatedly

        string source = await ReadFixtureAsync("Minimal.fx", cts.Token);
        var result = compiler.Compile(source, BuildOptions("Minimal.fx", PlatformTarget.OpenGL), cts.Token);

        AssertSuccess(result, "Minimal.fx", "sync after InitializeAsync");
        result.Value.Data.Should().NotBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Implementation
    // -------------------------------------------------------------------------

    private static async Task AssertSyncEqualsAsyncAsync(
        IEnumerable<string> fixtures,
        PlatformTarget target,
        CancellationToken ct)
    {
        var compiler = new EffectCompiler();

        foreach (string fx in fixtures)
        {
            string source = await ReadFixtureAsync(fx, ct);
            CompilerOptions options = BuildOptions(fx, target);

            // Sync arm FIRST, on this thread — no Task involved anywhere in the call.
            Result<CompiledShader, ShaderError[]> syncResult = compiler.Compile(source, options, ct);
            Result<CompiledShader, ShaderError[]> asyncResult = await compiler.CompileAsync(source, options, ct);

            AssertSuccess(syncResult, fx, $"sync/{target}");
            AssertSuccess(asyncResult, fx, $"async/{target}");

            syncResult.Value.Data.Should().Equal(asyncResult.Value.Data,
                because: $"'{fx}' for {target} must compile to byte-identical output through " +
                         "Compile and CompileAsync — they share ONE pipeline core (issue #28); " +
                         "a mismatch means the implementations forked");
        }
    }

    private static CompilerOptions BuildOptions(
        string fx, PlatformTarget target, DxbcBackend backend = DxbcBackend.Vkd3d) => new()
    {
        Target = target,
        // Fixed fixture-relative name (diagnostics only; no corpus fixture uses #include).
        SourceFileName = fx,
        // vkd3d by default so the DirectX rows run identically on every OS
        // (CrossHostByteIdentityTests' choice); the oracle test overrides explicitly.
        DxbcBackend = backend,
    };

    private static async Task<string> ReadFixtureAsync(string fx, CancellationToken ct) =>
        (await File.ReadAllTextAsync(TestHelpers.FixturePath(fx), ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertSuccess(
        Result<CompiledShader, ShaderError[]> result, string fx, string arm)
    {
        result.IsSuccess.Should().BeTrue(
            because: $"'{fx}' ({arm}) is in the corpus and must compile; errors: " +
                     (result.IsFailure
                         ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}"))
                         : "<none>"));
    }
}

/// <summary>
/// Skips when not on Windows — for the d3dcompiler_47 oracle backend, which is
/// Windows-only by nature (the cross-platform rows pin vkd3d instead). Mirrors
/// <c>tests/ShadowDusk.HLSL.Tests/D3DCompiler/WindowsFactAttribute.cs</c>.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "DXBC oracle backend requires Windows (d3dcompiler_47.dll).";
    }
}
