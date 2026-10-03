#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The desktop and the browser must surface the SAME <c>CompiledShader.Warnings</c> for a
/// compile that succeeds with a non-fatal vkd3d diagnostic (issue #335).
///
/// <para>The browser vkd3d host used to return the bytes and drop vkd3d's message text on
/// success, so <c>ImplicitTruncationWarning.fx</c> compiled to the desktop's exact bytes
/// there with an empty <c>Warnings</c> list. The text now crosses the shim beside the bytes
/// and both hosts run <c>Vkd3dCompileContract.MapCompileWarnings</c> +
/// <c>Vkd3dSourceLocator.Relocate</c>.</para>
///
/// <para>This class pins the DESKTOP half of the contract on the real pipeline: the fixture
/// produces exactly one <c>W5300</c> on the author's line and column, identically on the two
/// vkd3d targets, in the canonical text the cross-host <c>warnings-manifest.json</c> records
/// (<see cref="ShaderError.FxcFormattedMessage"/>). The browser half is gated where a browser
/// can run: <c>node-test-vkd3d-wasm.mjs</c> requires the shim to hand back the desktop's
/// verbatim message text on every corpus compile, and <c>browser-vkd3d-gate.mjs</c> compiles
/// this fixture through the real <c>WasmShaderCompiler</c> against that manifest.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class Vkd3dWarningsParityTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>What both hosts must report for the fixture: `float3 rgb = color;` on line 47.</summary>
    private const string ExpectedWarning =
        "ImplicitTruncationWarning.fx(47,12-12): warning W5300: Implicit truncation of vector type.";

    private static async Task<CompiledShader> CompileFixtureAsync(PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        string source = (await File.ReadAllTextAsync(TestHelpers.FixturePath("ImplicitTruncationWarning.fx"), cts.Token))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target         = target,
            SourceFileName = "ImplicitTruncationWarning.fx",
            DxbcBackend    = DxbcBackend.Vkd3d,
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            $"ImplicitTruncationWarning.fx must COMPILE for {target}: the truncation is a warning, never an error " +
            "(mgfxc never passes /WX). Errors: " +
            (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
        return result.Value;
    }

    [FnaFact]
    public async Task DirectXAndFna_SurfaceTheSameW5300_OnTheAuthorsLineAndColumn()
    {
        CompiledShader dx  = await CompileFixtureAsync(PlatformTarget.DirectX);
        CompiledShader fna = await CompileFixtureAsync(PlatformTarget.Fna);

        string[] dxWarnings  = dx.Warnings.Select(w => w.FxcFormattedMessage).ToArray();
        string[] fnaWarnings = fna.Warnings.Select(w => w.FxcFormattedMessage).ToArray();

        dxWarnings.ShouldBe(new[] { ExpectedWarning }, "the SM4 (DXBC_TPF) compile: one vkd3d warning, relocated through the macro prelude's #line onto the author's line");
        fnaWarnings.ShouldBe(new[] { ExpectedWarning }, "the SM3 (D3D_BYTECODE) compile: the same text at the same position");
        dxWarnings.ShouldBe(fnaWarnings, "the two vkd3d targets agree, so one warnings manifest line serves both");

        ShaderError w = dx.Warnings[0];
        w.Severity.ShouldBe(ShaderErrorSeverity.Warning, "never fatal");
        w.Code.ShouldBe("W5300");
        w.Message.ShouldBe("Implicit truncation of vector type.", customMessage: "vkd3d's text, verbatim (constraint 5)");
        w.Line.ShouldBe(47);
        w.Column.ShouldBe(12, customMessage: "the 'rgb' declarator vkd3d reports, on the author's indentation");
    }
}
