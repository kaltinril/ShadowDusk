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
/// cross-host byte-identity suites, so these tests pin only the call count, against the
/// real vkd3d.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "FNA")]
public sealed class Vkd3dRedundantCompileTests
{
    private sealed class CountingVkd3d : IDxbcShaderCompiler
    {
        private readonly Vkd3dShaderCompiler _inner = new();
        public List<(string Entry, ShaderStage Stage, string? Profile)> Requests { get; } = [];

        public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(Compile(request, cancellationToken));

        public Result<PlatformBlob, ShaderError> Compile(
            D3DCompileRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add((request.EntryPoint, request.Stage, request.ProfileOverride));
            return _inner.Compile(request, cancellationToken);
        }
    }

    [FnaTheory]
    [InlineData(PlatformTarget.DirectX)]
    [InlineData(PlatformTarget.Fna)]
    public async Task BasicEffect_CompilesEachDistinctEntryPointOnce(PlatformTarget target)
    {
        string path = TestHelpers.FixturePath("BasicEffect.fx");
        var counting = new CountingVkd3d();

        var result = await new EffectCompiler(dxbcCompilerFactory: () => counting).CompileAsync(
            await File.ReadAllTextAsync(path),
            new CompilerOptions { Target = target, SourceFileName = path });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error[0].Message : null);
        counting.Requests.Count.ShouldBe(counting.Requests.Distinct().Count(),
            "every request reaching vkd3d must be a distinct one");
        // 32 techniques x (VS + PS) name 30 distinct shaders.
        counting.Requests.Count.ShouldBe(30);
    }
}
