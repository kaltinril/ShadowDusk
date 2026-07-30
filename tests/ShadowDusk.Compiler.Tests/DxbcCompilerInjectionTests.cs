#nullable enable

using Shouldly;
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
            => Task.FromResult(Compile(request, cancellationToken));

        public Result<PlatformBlob, ShaderError> Compile(
            D3DCompileRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Result<PlatformBlob, ShaderError>.Fail(new ShaderError(
                File: request.SourceFileName,
                Line: 0,
                Column: 0,
                Code: "SDTEST",
                Message: "sentinel from injected dxbc compiler"));
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
            // Deliberately left at the default (Vkd3d): the injected factory is a
            // HOST decision and must take precedence over the desktop backend selector.
        });

        fake.Requests.ShouldHaveSingleItem("the single PS entry point must compile through the injected backend");
        D3DCompileRequest request = fake.Requests[0];
        request.Stage.ShouldBe(ShaderStage.Pixel);
        request.EntryPoint.ShouldBe("MainPS");
        request.ProfileOverride.ShouldBeNull("the DirectX target compiles at the SM5 stage default, never an override");

        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SDTEST").ShouldHaveSingleItem("the injected backend's error must propagate unswallowed (constraint 5)");
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

        fake.Requests.ShouldHaveSingleItem();
        D3DCompileRequest request = fake.Requests[0];
        request.Stage.ShouldBe(ShaderStage.Pixel);
        request.EntryPoint.ShouldBe("MainPS");
        request.ProfileOverride.ShouldBe("ps_2_0", customMessage: "the FNA path honors the pass's declared SM ≤ 3 profile verbatim — " +
                     "this is what makes the injected (WASM) backend emit D3D_BYTECODE");

        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SDTEST").ShouldHaveSingleItem();
    }

    /// <summary>
    /// Phase 42 (issue #28): the SYNCHRONOUS <see cref="EffectCompiler.Compile"/> must
    /// route through the exact same injected-backend seam as <c>CompileAsync</c> — there
    /// is ONE pipeline core; the sync entry is not a fork. Mirrors the async test above.
    /// </summary>
    [Fact]
    public void DirectXTarget_SyncCompile_RoutesThroughInjectedDxbcCompiler()
    {
        var fake = new RecordingFailingDxbcCompiler();
        var compiler = new EffectCompiler(dxbcCompilerFactory: () => fake);

        var result = compiler.Compile(DirectXEffect, new CompilerOptions
        {
            Target         = PlatformTarget.DirectX,
            SourceFileName = "inline.fx",
        });

        fake.Requests.ShouldHaveSingleItem();
        fake.Requests[0].Stage.ShouldBe(ShaderStage.Pixel);
        fake.Requests[0].EntryPoint.ShouldBe("MainPS");

        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SDTEST").ShouldHaveSingleItem();
    }

    /// <inheritdoc cref="DirectXTarget_SyncCompile_RoutesThroughInjectedDxbcCompiler"/>
    [Fact]
    public void FnaTarget_SyncCompile_RoutesThroughInjectedDxbcCompiler()
    {
        var fake = new RecordingFailingDxbcCompiler();
        var compiler = new EffectCompiler(dxbcCompilerFactory: () => fake);

        var result = compiler.Compile(FnaEffect, new CompilerOptions
        {
            Target         = PlatformTarget.Fna,
            SourceFileName = "inline.fx",
        });

        fake.Requests.ShouldHaveSingleItem();
        fake.Requests[0].ProfileOverride.ShouldBe("ps_2_0");

        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SDTEST").ShouldHaveSingleItem();
    }

    // NOTE deliberately absent: a "no injection still uses the desktop backends" test
    // would have to run the native vkd3d/d3dcompiler P/Invoke — not a pure unit test.
    // That default path is already pinned end-to-end by the Integration suite
    // (CrossHostByteIdentityTests, FnaCompileFixtureTests, EffectCompilerTests).

    // -------------------------------------------------------------------------
    // Phase 48 regression: the recognized-profile check (SD0013/SD0014) macro-
    // expands a profile macro via DXC's -P preprocessor. The WASM DXC shim has NO
    // -P export and THROWS NotSupportedException (JsShaderBackends.Preprocess). The
    // best-effort check must CATCH that and DEFER to the actual compile, never crash
    // or block a compile that would otherwise succeed. (Caught a real main-breaking
    // regression: the in-browser DX/FNA byte-identity gate threw on every macro-
    // profile shader.) Pure unit test — no native, no browser.
    // -------------------------------------------------------------------------

    private const string FnaMacroProfileEffect = """
        #define PS_SHADERMODEL ps_2_0
        float4 MainPS() : COLOR0
        {
            return float4(1, 0, 0, 1);
        }

        technique T
        {
            pass P
            {
                PixelShader = compile PS_SHADERMODEL MainPS();
            }
        }
        """;

    /// <summary>Mirrors the WASM DXC shim: <c>-P</c> preprocess throws; codegen never reached on FNA.</summary>
    private sealed class PreprocessThrowingDxcCompiler : IDxcShaderCompiler
    {
        public Task<Result<PlatformBlob, ShaderError>> CompileAsync(DxcCompileRequest r, CancellationToken ct = default)
            => throw new NotSupportedException("codegen must not be reached on the FNA (vkd3d) path");

        public Result<PlatformBlob, ShaderError> Compile(DxcCompileRequest r, CancellationToken ct = default)
            => throw new NotSupportedException("codegen must not be reached on the FNA (vkd3d) path");

        public Result<string, ShaderError> Preprocess(DxcPreprocessRequest r, CancellationToken ct = default)
            => throw new NotSupportedException(
                "DXC preprocess-only (-P) is not available on the WASM DXC backend yet.");
    }

    [Fact]
    public async Task MacroProfile_WhenPreprocessThrows_SkipsValidationAndCompiles()
    {
        var fakeDxbc = new RecordingFailingDxbcCompiler();
        var compiler = new EffectCompiler(
            dxcCompilerFactory:  () => new PreprocessThrowingDxcCompiler(),
            dxbcCompilerFactory: () => fakeDxbc);

        // Before the fix this THREW NotSupportedException out of profile validation.
        var result = await compiler.CompileAsync(FnaMacroProfileEffect, new CompilerOptions
        {
            Target         = PlatformTarget.Fna,
            SourceFileName = "inline.fx",
        });

        // The compile reached the dxbc backend — proof the macro-profile check skipped
        // (deferred) instead of throwing or rejecting when -P is unavailable.
        fakeDxbc.Requests.ShouldHaveSingleItem("an unavailable -P preprocessor must make the profile check defer, not block the compile");
        result.IsFailure.ShouldBeTrue();
        result.Error.Where(e => e.Code == "SDTEST").ShouldHaveSingleItem("the failure must come from the (injected) codegen backend, not the profile check");
        result.Error.ShouldNotContain(e => e.Code == "SD0013" || e.Code == "SD0014", "a defined macro profile must not be rejected just because -P could not run");
    }
}
