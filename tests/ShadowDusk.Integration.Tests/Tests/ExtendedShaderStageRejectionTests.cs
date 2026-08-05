#nullable enable

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 58 Area C - geometry / hull / domain / compute pass assignments must fail with the
/// registered <c>FX0014</c> diagnostic naming the stage and the permanent reason, on every
/// target, and must never write an output file.
/// </summary>
/// <remarks>
/// <para>
/// These inputs already failed before Phase 58; what was wrong was the <em>message</em>.
/// <c>FxPreParser</c> knew only <c>VertexShader</c>/<c>PixelShader</c>, so everything else fell
/// through to render-state parsing, which choked on the <c>compile</c> expression and reported
/// <c>FX0008 "Expected ';' after render-state 'HullShader = compile'"</c> - sending the user
/// hunting for a syntax error in a file that has none. So this is a reject-set <em>message</em>
/// change, not a reject-set change: no verdict moves and no output byte moves.
/// </para>
/// <para>
/// The reject set is mgfxc-faithful and was MEASURED, not assumed (2026-08-05, pinned mgfxc
/// 3.8.2.1105, <c>/Profile:DirectX_11</c>): mgfxc refuses all four stages in BOTH the
/// <c>compile</c> and the <c>NULL</c> form, pointing at the stage keyword. The
/// <c>NULL</c> arm is covered here because <c>VertexShader = NULL;</c> IS accepted (fxc parity,
/// bug-hunt 2026-07-27 M14), so it is a genuinely separate branch.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ExtendedShaderStageRejectionTests
{
    /// <summary>Every stage x assignment-form x target cell (4 x 2 x 3 = 24).</summary>
    public static TheoryData<string, string, string, string> RejectedCells()
    {
        var data = new TheoryData<string, string, string, string>();

        (string Key, string Profile, string Name)[] stages =
        [
            ("HullShader",     "hs_5_0", "hull"),
            ("DomainShader",   "ds_5_0", "domain"),
            ("GeometryShader", "gs_4_0", "geometry"),
            ("ComputeShader",  "cs_5_0", "compute"),
        ];

        foreach (var (key, profile, name) in stages)
        {
            foreach (string assignment in new[] { $"compile {profile} StageEntry()", "NULL" })
            {
                // OpenGL and DirectX_11 are the two shipped MonoGame/KNI routes; FNA is the
                // separate fx_2_0 writer, which takes a different pre-parser mode
                // (PreserveSm3) and so is a distinct path through the same guard.
                foreach (string target in new[] { "OpenGL", "DirectX_11", "FNA" })
                    data.Add(key, assignment, name, target);
            }
        }

        return data;
    }

    // The standard cross-platform shader-model header, SM4-gated rather than the stock
    // '#if OPENGL' split: the '#else' arm also catches the SM3-capped FNA target (Phase 51
    // A10). Without it the DirectX_11 cells would fail on SD0015 (profile floor) instead of
    // the stage guard, which would make this suite pass for the wrong reason.
    private const string ShaderModelHeader =
        """
        #if SM4
        #define VS_SHADERMODEL vs_4_0_level_9_1
        #define PS_SHADERMODEL ps_4_0_level_9_1
        #else
        #define VS_SHADERMODEL vs_3_0
        #define PS_SHADERMODEL ps_3_0
        #endif
        """;

    private static string SourceFor(string stageKey, string assignment) =>
        $$"""
        {{ShaderModelHeader}}
        float4 MainVS(float4 pos : POSITION) : POSITION { return pos; }
        float4 MainPS() : COLOR0 { return float4(1, 0, 0, 1); }
        technique T
        {
            pass P
            {
                VertexShader = compile VS_SHADERMODEL MainVS();
                PixelShader = compile PS_SHADERMODEL MainPS();
                {{stageKey}} = {{assignment}};
            }
        }
        """;

    [Theory]
    [MemberData(nameof(RejectedCells))]
    public async Task UnloadableStage_FailsWithFX0014_AndWritesNoOutput(
        string stageKey, string assignment, string stageName, string target)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string tempDir    = Path.Combine(Path.GetTempPath(), $"shadowdusk_p58_{Guid.NewGuid():N}");
        string inputPath  = Path.Combine(tempDir, "stage_test.fx");
        string outputPath = Path.Combine(tempDir, "stage_test.mgfx");
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(inputPath, SourceFor(stageKey, assignment), cts.Token);

            var result = await TestHelpers.CompileViaPipelineAsync(
                inputPath, outputPath, target, cts.Token);

            result.ExitCode.ShouldBe(1);
            result.Mgfx.ShouldBeEmpty();

            // "no output file is written" is the acceptance wording, and it is worth
            // asserting directly rather than trusting the empty byte array: a compiler that
            // reported an error but still left a stale/partial artifact on disk would let a
            // build script ship something unloadable.
            File.Exists(outputPath).ShouldBeFalse(
                $"a rejected effect must leave no output file ({target})");

            result.Stderr.ShouldContain("FX0014", Case.Sensitive);
            result.Stderr.ShouldContain(stageKey, Case.Sensitive);
            result.Stderr.ShouldContain(stageName, Case.Sensitive);

            // The regression itself: the old, wrong diagnostic must not come back.
            result.Stderr.ShouldNotContain("FX0008", Case.Sensitive);

            // Still MGCB-parseable - the drop-in-mgfxc promise is the diagnostic FORMAT,
            // so a better message must not cost the machine-readable shape.
            result.Stderr.ShouldMatch(@"\.fx\(\d+,\d+(-\d+)?\): error FX0014:");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* non-fatal */ }
        }
    }

    [Theory]
    [InlineData("OpenGL")]
    [InlineData("DirectX_11")]
    [InlineData("FNA")]
    public async Task PassWithOnlyVertexAndPixelStages_StillCompiles(string target)
    {
        // The control arm. The guard sits in the pass-key loop ahead of the '=' consume, so
        // this pins that the ordinary two-stage pass it now precedes is untouched on every
        // target - i.e. that the reject set really did not move.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        string tempDir    = Path.Combine(Path.GetTempPath(), $"shadowdusk_p58ok_{Guid.NewGuid():N}");
        string inputPath  = Path.Combine(tempDir, "ok_shader.fx");
        string outputPath = Path.Combine(tempDir, "ok_shader.mgfx");
        Directory.CreateDirectory(tempDir);

        try
        {
            string source =
                $$"""
                {{ShaderModelHeader}}
                float4 MainVS(float4 pos : POSITION) : POSITION { return pos; }
                float4 MainPS() : COLOR0 { return float4(1, 0, 0, 1); }
                technique T
                {
                    pass P
                    {
                        CullMode = None;
                        VertexShader = compile VS_SHADERMODEL MainVS();
                        PixelShader = compile PS_SHADERMODEL MainPS();
                    }
                }
                """;

            await File.WriteAllTextAsync(inputPath, source, cts.Token);

            var result = await TestHelpers.CompileViaPipelineAsync(
                inputPath, outputPath, target, cts.Token);

            result.ExitCode.ShouldBe(0, customMessage: $"stderr: {result.Stderr}");
            result.Mgfx.Length.ShouldBeGreaterThan(0);
            Regex.IsMatch(result.Stderr, "FX0014").ShouldBeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* non-fatal */ }
        }
    }
}
