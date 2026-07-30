#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using ShadowDusk.Core.Preprocessor;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 45 <b>B10</b>: the OpenGL cbuffer/parameter "offset bridge".
///
/// <para>A free uniform whose name collides with a GLSL reserved word (the canonical
/// case is <c>float noise;</c>, which clashes with GLSL's deprecated <c>noiseN()</c>
/// builtins) is renamed by SPIRV-Cross to <c>_noise</c> so the emitted GLSL is legal.
/// <c>CompilationPipeline</c> previously joined the rewriter's GLSL uniform layout to
/// the reflected effect-parameter list <b>by name</b>, so the GLSL side's <c>_noise</c>
/// found no <c>noise</c> on the parameter side and the loud <c>SD0012</c> guard fired —
/// even though <c>noise</c> is valid HLSL that fxc/mgfxc accept (it compiles fine on DX
/// and FNA). The fix adds an OFFSET BRIDGE that runs <b>only on a name miss</b>: the GL
/// uniform's <c>BaseRegister * 16</c> byte offset locates the reflected cbuffer variable
/// (which still carries the ORIGINAL name), recovering the parameter index without ever
/// trusting the SPIRV-Cross spelling. The parameter stays exposed under <c>noise</c>.</para>
///
/// <para>These tests assert the BEHAVIOUR the bridge guarantees: (1) the reserved-word
/// shader now compiles on OpenGL and exposes the parameter under its original name
/// <c>noise</c> at the correct cbuffer offset, bound to the <c>ps_uniforms_vec4</c>
/// record; (2) the CONTROL — an ordinary free uniform whose name needs no rename —
/// resolves by name exactly as before, so the bridge never perturbs the common path
/// (and the cross-host byte-identity corpus stays unchanged). Real DXC + SPIRV-Cross
/// run, so the class is tagged Integration.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "OpenGL")]
public sealed class ReservedWordUniformBridgeTests
{
    private const byte ProfileOpenGL = 0; // MgfxProfile.OpenGL
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    // A free uniform named after the GLSL reserved word `noise`, USED in the pixel body
    // (so DXC cannot strip it). On OpenGL SPIRV-Cross renames it to `_noise`; the offset
    // bridge keeps the .mgfx parameter named `noise`.
    private const string ReservedWordSource = """
        sampler s0;

        float noise;

        float4 PSMain(float2 uv : TEXCOORD0) : COLOR0
        {
            float4 c = tex2D(s0, uv);
            c.rgb += (frac(sin(dot(uv, float2(12.9898, 78.233))) * 43758.5453) - 0.5) * noise;
            return c;
        }

        technique T
        {
            pass P
            {
                PixelShader = compile ps_3_0 PSMain();
            }
        }
        """;

    // CONTROL: an ordinary free uniform whose name is NOT a GLSL reserved word, so the
    // primary name join resolves it and the bridge is never consulted.
    private const string PlainUniformSource = """
        sampler s0;

        float Intensity;

        float4 PSMain(float2 uv : TEXCOORD0) : COLOR0
        {
            float4 c = tex2D(s0, uv);
            c.rgb *= Intensity;
            return c;
        }

        technique T
        {
            pass P
            {
                PixelShader = compile ps_3_0 PSMain();
            }
        }
        """;

    private static async Task<Result<CompiledShader, ShaderError[]>> CompileGlAsync(
        string source, CancellationToken ct)
    {
        var options = new CompilerOptions
        {
            Target          = PlatformTarget.OpenGL,
            IncludeResolver = new FileSystemIncludeResolver(),
        };
        return await new EffectCompiler().CompileAsync(source, options, ct);
    }

    private static string DescribeErrors(Result<CompiledShader, ShaderError[]> r) =>
        r.IsFailure ? string.Join(" | ", r.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>";

    // -------------------------------------------------------------------------
    // The bridge case: reserved-word uniform resolves under its original name.
    // -------------------------------------------------------------------------

    [DxcFact]
    public async Task ReservedWordUniform_OpenGL_CompilesAndExposesParameterUnderOriginalName()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileGlAsync(ReservedWordSource, cts.Token);

        result.IsSuccess.ShouldBeTrue($"the B10 offset bridge must resolve the renamed '_noise' uniform back to " +
                     $"the 'noise' parameter (no more SD0012); errors: {DescribeErrors(result)}");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.ProfileId.ShouldBe(ProfileOpenGL);

        // The parameter is exposed under the ORIGINAL HLSL name — never the SPIRV-Cross
        // rename — so the consumer's effect.Parameters["noise"].SetValue(...) binds.
        reader.ParameterNames.ShouldContain("noise", "the offset bridge recovers the parameter index but the .mgfx parameter " +
                     "keeps its original declared name");
        reader.ParameterNames.ShouldNotContain("_noise", "the SPIRV-Cross reserved-word rename must never leak into the parameter table");

        // The single $Globals free uniform packs into ps_uniforms_vec4[0] -> byte offset 0,
        // and that cbuffer record must point at the 'noise' parameter index.
        reader.ParameterOffsets.ContainsKey("noise").ShouldBeTrue();
        reader.ParameterOffsets["noise"].ShouldBe(0, customMessage: "'noise' is the only free uniform, so it occupies register 0 (byte offset 0)");

        int noiseIndex = reader.ParameterNames
            .Select((n, i) => (n, i)).First(t => t.n == "noise").i;
        MgfxConstantBufferRecord ps = reader.ConstantBuffers
            .Where(c => c.Name == "ps_uniforms_vec4").ShouldHaveSingleItem("the pixel free-uniform cbuffer is named ps_uniforms_vec4");
        ps.ParameterIndices.ShouldContain(noiseIndex, "the ps_uniforms_vec4 record must reference the 'noise' parameter by index " +
                     "so MonoGame uploads its value into register 0");
    }

    [Theory]
    [Trait("Platform", "OpenGL")]
    [InlineData("OpenGL", ProfileOpenGL)]
    public async Task ReservedWordUniform_FixtureFile_OpenGL_Compiles(string profile, byte expectedProfileId)
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        // The committed regression fixture (the same shape, project-owned).
        var result = await TestHelpers.CompileFixtureAsync(
            "examples/ExReservedWordUniform.fx", profile, ct: cts.Token);

        result.ExitCode.ShouldBe(0, customMessage: $"ExReservedWordUniform.fx (B10 regression) must compile on {profile}; stderr: {result.Stderr}");

        var reader = MgfxBlobReader.Parse(result.Mgfx);
        reader.ProfileId.ShouldBe(expectedProfileId);
        reader.ParameterNames.ShouldContain("noise");
        reader.ParameterNames.ShouldNotContain("_noise");
    }

    // -------------------------------------------------------------------------
    // The control: an ordinary uniform is unaffected (bridge never runs).
    // -------------------------------------------------------------------------

    [DxcFact]
    public async Task PlainUniform_OpenGL_ResolvesByName_BridgeNotNeeded()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await CompileGlAsync(PlainUniformSource, cts.Token);

        result.IsSuccess.ShouldBeTrue($"an ordinary free uniform must resolve by the primary NAME match, the same " +
                     $"path it always took; errors: {DescribeErrors(result)}");

        var reader = MgfxBlobReader.Parse(result.Value.Data);
        reader.ParameterNames.ShouldContain("Intensity", "the uniform name needs no rename, so it is matched and exposed directly");
        reader.ParameterOffsets.ContainsKey("Intensity").ShouldBeTrue();
        reader.ParameterOffsets["Intensity"].ShouldBe(0);
    }
}
