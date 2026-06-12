#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// PURE unit tests (no disk, no process, no native compiler) for the Phase 42 / issue #28
/// synchronous API surface on <see cref="EffectCompiler"/>:
/// <c>InitializeAsync</c> (desktop: a documented no-op — completes, idempotent, honors
/// cancellation) and the synchronous <c>Compile</c> (mirrors <c>CompileAsync</c> exactly —
/// one shared pipeline core). The compile assertions use failure paths the pipeline
/// resolves BEFORE any native backend (the Metal SD0200 reject and the no-techniques
/// SD0010), keeping the tests pure while proving both entry points traverse the same code.
/// The full-corpus byte-identity gate lives in the Integration suite
/// (<c>SyncCompileByteIdentityTests</c>).
/// </summary>
public sealed class SyncCompileApiTests
{
    private const string NoTechniqueSource = """
        float4 MainPS() : SV_TARGET
        {
            return float4(1, 0, 0, 1);
        }
        """;

    [Fact]
    public async Task InitializeAsync_Completes_And_IsIdempotent()
    {
        var compiler = new EffectCompiler();

        // Desktop contract: effectively a no-op (natives load on first use), idempotent,
        // safe to await repeatedly — and completes synchronously, so awaiting it from a
        // single-threaded context can never deadlock.
        await compiler.InitializeAsync();
        await compiler.InitializeAsync();

        compiler.InitializeAsync().IsCompletedSuccessfully.Should().BeTrue(
            because: "the desktop InitializeAsync is a documented no-op");
    }

    [Fact]
    public async Task InitializeAsync_HonorsCancellation()
    {
        var compiler = new EffectCompiler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Task initialize = compiler.InitializeAsync(cts.Token);

        initialize.IsCanceled.Should().BeTrue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
    }

    [Fact]
    public async Task Compile_MetalTarget_SameSd0200Error_AsCompileAsync()
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions { Target = PlatformTarget.Metal };

        var syncResult  = compiler.Compile(NoTechniqueSource, options);
        var asyncResult = await compiler.CompileAsync(NoTechniqueSource, options);

        syncResult.IsFailure.Should().BeTrue();
        syncResult.Error.Should().ContainSingle(e => e.Code == "SD0200");

        // One pipeline core ⇒ identical diagnostics from both entry points.
        asyncResult.IsFailure.Should().BeTrue();
        asyncResult.Error.Should().BeEquivalentTo(syncResult.Error);
    }

    [Fact]
    public async Task Compile_NoTechniques_SameSd0010Error_AsCompileAsync()
    {
        var compiler = new EffectCompiler();
        var options = new CompilerOptions
        {
            Target = PlatformTarget.OpenGL,
            SourceFileName = "inline.fx",
        };

        var syncResult  = compiler.Compile(NoTechniqueSource, options);
        var asyncResult = await compiler.CompileAsync(NoTechniqueSource, options);

        syncResult.IsFailure.Should().BeTrue();
        syncResult.Error.Should().ContainSingle(e => e.Code == "SD0010");

        asyncResult.IsFailure.Should().BeTrue();
        asyncResult.Error.Should().BeEquivalentTo(syncResult.Error);
    }

    [Fact]
    public void Compile_AlreadyCancelled_Throws_OperationCanceled()
    {
        var compiler = new EffectCompiler();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action act = () => compiler.Compile(
            NoTechniqueSource, new CompilerOptions { Target = PlatformTarget.OpenGL }, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
