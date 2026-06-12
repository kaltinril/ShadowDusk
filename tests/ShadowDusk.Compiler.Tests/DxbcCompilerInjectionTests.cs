#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// PURE unit tests (no disk, no process, no native compiler) for the Phase 4.1
/// <c>dxbcCompilerFactory</c> injection seam on <see cref="EffectCompiler"/>: an
/// injected <see cref="IDxbcShaderCompiler"/> must receive BOTH the DirectX target's
/// SM5 requests and the FNA target's SM ≤ 3 requests — this is the seam through which
/// the browser/WASM host routes the vkd3d→WASM backend (<c>WasmVkd3dShaderCompiler</c>),
/// and through which the byte-identity corpus probe records desktop ground truth.
/// The fake fails every compile, so the pipeline never reaches reflection or writers —
/// keeping the tests pure while still proving the routing and the error propagation.
/// </summary>
public sealed class DxbcCompilerInjectionTests
{
    private const string DirectXEffect = """
        float4 MainPS() : SV_TARGET
        {
            return float4(1, 0, 0, 1);
        }

        technique T
        {
            pass P
            {
                PixelShader = compile ps_4_0 MainPS();
            }
        }
        """;

    private const string FnaEffect = """
        float4 MainPS() : COLOR0
        {
            return float4(1, 0, 0, 1);
        }

        technique T
        {
            pass P
            {
                PixelShader = compile ps_2_0 MainPS();
            }
        }
        """;

    /// <summary>Records every request, then fails with a recognizable sentinel error.</summary>
    private sealed class RecordingFailingDxbcCompiler : IDxbcShaderCompiler
    {
        public List<D3DCompileRequest> Requests { get; } = [];

        public Task<Result<PlatformBlob, ShaderError>> CompileAsync(
            D3DCompileRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                File: request.SourceFileName,
                Line: 0,
                Column: 0,
                Code: "SDTEST",
                Message: "sentinel from injected dxbc compiler")));
        }
    }

    [Fact]
    public async Task DirectXTarget_RoutesThroughInjectedDxbcCompiler_WithSm5Request()
    {
        var fake = new RecordingFailingDxbcCompiler();
        var compiler = new EffectCompiler(dxbcCompilerFactory: () => fake);

        var result = await compiler.CompileAsync(DirectXEffect, new CompilerOptions
        {
            Target         = PlatformTarget.DirectX,
            SourceFileName = "inline.fx",
            // Deliberately left at the default (D3DCompiler): the injected factory is a
            // HOST decision and must take precedence over the desktop backend selector.
        });

        fake.Requests.Should().ContainSingle(
            because: "the single PS entry point must compile through the injected backend");
        D3DCompileRequest request = fake.Requests[0];
        request.Stage.Should().Be(ShaderStage.Pixel);
        request.EntryPoint.Should().Be("MainPS");
        request.ProfileOverride.Should().BeNull(
            because: "the DirectX target compiles at the SM5 stage default, never an override");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainSingle(e => e.Code == "SDTEST",
            because: "the injected backend's error must propagate unswallowed (constraint 5)");
    }

    [Fact]
    public async Task FnaTarget_RoutesThroughInjectedDxbcCompiler_WithSm3ProfileOverride()
    {
        var fake = new RecordingFailingDxbcCompiler();
        var compiler = new EffectCompiler(dxbcCompilerFactory: () => fake);

        var result = await compiler.CompileAsync(FnaEffect, new CompilerOptions
        {
            Target         = PlatformTarget.Fna,
            SourceFileName = "inline.fx",
        });

        fake.Requests.Should().ContainSingle();
        D3DCompileRequest request = fake.Requests[0];
        request.Stage.Should().Be(ShaderStage.Pixel);
        request.EntryPoint.Should().Be("MainPS");
        request.ProfileOverride.Should().Be("ps_2_0",
            because: "the FNA path honors the pass's declared SM ≤ 3 profile verbatim — " +
                     "this is what makes the injected (WASM) backend emit D3D_BYTECODE");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().ContainSingle(e => e.Code == "SDTEST");
    }

    // NOTE deliberately absent: a "no injection still uses the desktop backends" test
    // would have to run the native vkd3d/d3dcompiler P/Invoke — not a pure unit test.
    // That default path is already pinned end-to-end by the Integration suite
    // (CrossHostByteIdentityTests, FnaCompileFixtureTests, EffectCompilerTests).
}
