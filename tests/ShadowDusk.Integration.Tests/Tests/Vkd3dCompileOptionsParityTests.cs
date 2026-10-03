#nullable enable

using System.Text;
using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.HLSL.Vkd3d;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// The desktop and the browser must hand vkd3d-shader the SAME compile options (issue #295).
///
/// <para>The browser's WASM wrapper used to pass <c>options = NULL</c> while the desktop
/// passed <c>BACKWARD_COMPATIBILITY</c> = <c>MAP_SEMANTIC_NAMES</c> for the SM4+ target, so
/// a shader with SM1-3 semantics on struct fields compiled to different DXBC in the browser,
/// or was refused there (<c>E5013</c>). The option list now has one owner,
/// <see cref="Vkd3dCompileContract.ResolveCompileOptions"/>: the browser backend
/// (<c>WasmVkd3dShaderCompiler</c>) sends exactly that list through its shim, and the
/// wrapper forwards it untouched.</para>
///
/// <para>These tests close the desktop half: the options the desktop backend REALLY hands
/// <c>vkd3d_shader_compile</c>, read back from the marshalled
/// <c>vkd3d_shader_compile_info</c> (<see cref="Vkd3dShaderCompiler.NativeOptionsObserver"/>),
/// must equal the contract's list for the target. A desktop that grew an option of its own
/// (which is how the two hosts came apart) fails here. The browser half is gated where a
/// browser can run: <c>node-test-vkd3d-wasm.mjs</c> replays the desktop's observed options
/// through the product shim and <c>browser-vkd3d-gate.mjs</c> compiles
/// <c>Sm3SemanticStructs.fx</c> through the real <c>WasmShaderCompiler</c>, both
/// byte-compared against the desktop.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class Vkd3dCompileOptionsParityTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Compiles <paramref name="fixture"/> through the real pipeline with the vkd3d backend
    /// and returns the option list of every native <c>vkd3d_shader_compile</c> call it made.
    /// </summary>
    private static async Task<(byte[] Output, List<int[]> NativeOptions)> CompileObservingOptionsAsync(
        string fixture, PlatformTarget target)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);
        string source = (await File.ReadAllTextAsync(TestHelpers.FixturePath(fixture), cts.Token))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var observed = new List<int[]>();
        Vkd3dShaderCompiler.NativeOptionsObserver.Value = options =>
        {
            lock (observed)
                observed.Add(options);
        };
        try
        {
            var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
            {
                Target         = target,
                SourceFileName = fixture,
                DxbcBackend    = DxbcBackend.Vkd3d,
            }, cts.Token);

            result.IsSuccess.ShouldBeTrue(
                $"'{fixture}' must compile for {target} with the vkd3d backend. Errors: " +
                (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
            return (result.Value.Data, observed);
        }
        finally
        {
            Vkd3dShaderCompiler.NativeOptionsObserver.Value = null;
        }
    }

    [FnaFact]
    public async Task Desktop_HandsVkd3d_ExactlyTheContractOptions_OnTheSm4Target()
    {
        (_, List<int[]> native) = await CompileObservingOptionsAsync("Sm3SemanticStructs.fx", PlatformTarget.DirectX);

        int[] contract = Vkd3dCompileContract.FlattenCompileOptions(
            Vkd3dCompileContract.ResolveCompileOptions(Vkd3dCompileContract.TargetTypeDxbcTpf));

        native.Count.ShouldBeGreaterThanOrEqualTo(2, "a VS+PS effect is at least two vkd3d compiles");
        foreach (int[] options in native)
        {
            options.ShouldBe(contract, customMessage:
                "the desktop handed vkd3d_shader_compile an option list that is not " +
                "Vkd3dCompileContract.ResolveCompileOptions(DXBC_TPF). The browser backend sends exactly " +
                "the contract's list, so the two hosts would compile differently (issue #295). Add or " +
                "change an option in the contract, never in a host.");
        }

        contract.ShouldBe([8, 1], customMessage:
            "BACKWARD_COMPATIBILITY (8) = MAP_SEMANTIC_NAMES (1) is what makes SM1-3 semantics legal at SM4+");
    }

    [FnaFact]
    public async Task Desktop_HandsVkd3d_NoOptions_OnTheSm3Target()
    {
        (_, List<int[]> native) = await CompileObservingOptionsAsync("Sm3SemanticStructs.fx", PlatformTarget.Fna);

        int[] contract = Vkd3dCompileContract.FlattenCompileOptions(
            Vkd3dCompileContract.ResolveCompileOptions(Vkd3dCompileContract.TargetTypeD3dBytecode));

        native.Count.ShouldBeGreaterThanOrEqualTo(2, "a VS+PS effect is at least two vkd3d compiles");
        foreach (int[] options in native)
            options.ShouldBe(contract, customMessage: "the desktop must pass the contract's list for D3D_BYTECODE too");
        contract.ShouldBeEmpty("POSITION / COLOR are the native semantics on the SM1-3 target");
    }

    /// <summary>
    /// What the option buys, on the fixture the browser gates replay: every SM1-3 struct
    /// semantic becomes its system value. Without the option the vertex position stays a
    /// plain <c>POSITION</c> (no system value, so nothing reaches the rasterizer) and the
    /// pixel shader is refused. Pinned on the bytes so that the corpus fixture cannot stop
    /// depending on the option without this failing first.
    /// </summary>
    [FnaFact]
    public async Task Sm3SemanticStructs_CompilesToSystemValueSemantics_OnTheSm4Target()
    {
        (byte[] mgfx, _) = await CompileObservingOptionsAsync("Sm3SemanticStructs.fx", PlatformTarget.DirectX);

        // The DXBC signature chunks carry the semantic names as ASCII strings.
        string text = Encoding.Latin1.GetString(mgfx);
        text.ShouldContain("SV_Position", Case.Sensitive,
            "the vertex OUTPUT / pixel INPUT 'POSITION0' struct field must be mapped to SV_Position");
        text.ShouldContain("SV_Target", Case.Sensitive,
            "the pixel OUTPUT 'COLOR0' struct field must be mapped to SV_Target");
    }
}
