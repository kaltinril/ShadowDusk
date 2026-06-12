#nullable enable

using FluentAssertions;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The collection definition backing <c>[Collection("Determinism")]</c>. Without this,
/// xUnit silently creates an implicit collection: the tests still serialize, but the
/// collection has no declared definition to hang fixtures or behavior on, and a typo in
/// the name would fork a second collection unnoticed. Declared explicitly so the
/// collection is a real, single, referencable thing.
/// </summary>
[CollectionDefinition("Determinism")]
public sealed class DeterminismCollection
{
}

/// <summary>
/// Determinism is guaranteed only for the same DXC + SPIRV-Cross binary versions within
/// a single CI run. These tests do not assert cross-version stability.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Determinism")]
[Collection("Determinism")]
public sealed class DeterminismTests
{
    [Fact]
    public async Task Minimal_OpenGL_ByteIdenticalOnSecondCompile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first  = await TestHelpers.CompileFixtureAsync("Minimal.fx", "OpenGL", ct: cts.Token);
        var second = await TestHelpers.CompileFixtureAsync("Minimal.fx", "OpenGL", ct: cts.Token);

        first.ExitCode.Should().Be(0, because: $"first compile must succeed; stderr: {first.Stderr}");
        second.ExitCode.Should().Be(0, because: $"second compile must succeed; stderr: {second.Stderr}");

        first.Mgfx.Should().Equal(second.Mgfx, because: "output must be byte-identical across two compilations");
    }

    [Fact]
    public async Task Minimal_DirectX11_ByteIdenticalOnSecondCompile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first  = await TestHelpers.CompileFixtureAsync("Minimal.fx", "DirectX_11", ct: cts.Token);
        var second = await TestHelpers.CompileFixtureAsync("Minimal.fx", "DirectX_11", ct: cts.Token);

        first.ExitCode.Should().Be(0, because: $"first compile must succeed; stderr: {first.Stderr}");
        second.ExitCode.Should().Be(0, because: $"second compile must succeed; stderr: {second.Stderr}");

        first.Mgfx.Should().Equal(second.Mgfx, because: "output must be byte-identical across two compilations");
    }

    [Fact]
    public async Task CBuffer_OpenGL_ByteIdenticalOnSecondCompile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first  = await TestHelpers.CompileFixtureAsync("cbuffer.fx", "OpenGL", ct: cts.Token);
        var second = await TestHelpers.CompileFixtureAsync("cbuffer.fx", "OpenGL", ct: cts.Token);

        first.ExitCode.Should().Be(0, because: $"first compile must succeed; stderr: {first.Stderr}");
        second.ExitCode.Should().Be(0, because: $"second compile must succeed; stderr: {second.Stderr}");

        first.Mgfx.Should().Equal(second.Mgfx,
            because: "constant buffer offset order must be deterministic across runs");
    }

    [Fact]
    public async Task Multitechnique_OpenGL_ByteIdenticalOnSecondCompile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first  = await TestHelpers.CompileFixtureAsync("multitechnique.fx", "OpenGL", ct: cts.Token);
        var second = await TestHelpers.CompileFixtureAsync("multitechnique.fx", "OpenGL", ct: cts.Token);

        first.ExitCode.Should().Be(0, because: $"first compile must succeed; stderr: {first.Stderr}");
        second.ExitCode.Should().Be(0, because: $"second compile must succeed; stderr: {second.Stderr}");

        first.Mgfx.Should().Equal(second.Mgfx,
            because: "technique and pass ordering must be unaffected by dictionary/hash iteration order");
    }
}
