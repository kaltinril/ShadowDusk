#nullable enable

using Shouldly;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// SM1-3 semantics on the SM4+ DirectX target: `fxc` accepts them, so ShadowDusk must too.
///
/// <para>That is how every MonoGame <c>.fx</c> written against Shader Model 3 still builds at
/// <c>ps_4_0</c>. vkd3d only accepts them with
/// <c>BACKWARD_COMPATIBILITY</c>/<c>MAP_SEMANTIC_NAMES</c>, which ShadowDusk passes on the
/// DXBC_TPF target; from vkd3d 2.1 it rejects them outright without it
/// (<c>E5013: Invalid semantic 'COLOR'</c>).</para>
///
/// <para><b>Why a struct is the case that matters.</b> <c>FxPreParser</c>'s
/// <c>RewriteToSm4</c> mode already retargets the <c>) : COLOR&lt;n&gt;</c> RETURN semantic, so
/// a plain <c>float4 PS(...) : COLOR</c> never reaches vkd3d with a legacy semantic. It
/// deliberately does NOT touch the same semantic on a struct FIELD, because the struct may be a
/// VERTEX shader's output, where <c>COLOR</c> is legal. A pixel shader returning
/// <c>struct { float4 c : COLOR0; }</c> is therefore the shape only the compile option covers,
/// and it is a real MonoGame idiom (deferred/MRT effects).</para>
///
/// <para>Nothing else pins this. <c>DeferredSprite.fx</c>'s other tests are OpenGL-only (see
/// <c>HidefGeneralityFixtureTests.DeferredSprite_Mrt_CompilesOnGl_EmitsFragDataOutputs_Gap2</c>,
/// the GL-side sibling of this problem), and DirectX output is not in the cross-host
/// byte-identity manifest for this fixture — so without these tests, dropping the option is a
/// silent regression on a shipping path.</para>
/// </summary>
[Trait("Category", "Integration")]
[Trait("Platform", "DirectX")]
public sealed class Vkd3dBackcompatSemanticsTests
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The real MRT fixture: a struct with two legacy <c>COLOR</c> outputs.</summary>
    [FnaFact]
    public async Task StructPixelShaderOutput_WithLegacyColorSemantics_CompilesForDirectX11()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        var result = await TestHelpers.CompileFixtureAsync("DeferredSprite.fx", "DirectX_11", ct: cts.Token);

        result.ExitCode.ShouldBe(0, customMessage:
            "a pixel shader returning a struct with ': COLOR0'/': COLOR1' fields must compile for " +
            "DirectX_11 — fxc accepts those semantics at ps_4_0, and vkd3d only does with " +
            $"BACKWARD_COMPATIBILITY/MAP_SEMANTIC_NAMES. stderr: {result.Stderr}");
        result.Mgfx.Length.ShouldBeGreaterThan(0, "a zero-byte output would pass the exit-code check vacuously");
    }

    /// <summary>
    /// The same shape reduced to its essentials, so a failure points at the semantic rather than
    /// at anything else in a 60-line fixture. Single output, so it isolates the semantic from MRT.
    /// </summary>
    [FnaFact]
    public async Task InlineStructOutput_WithLegacyColorSemantic_CompilesForDirectX11()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        const string source = """
            texture SpriteTexture;
            sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };

            struct PSOutput
            {
                float4 Color : COLOR0;
            };

            PSOutput MainPS(float2 uv : TEXCOORD0)
            {
                PSOutput output;
                output.Color = tex2D(SpriteTextureSampler, uv);
                return output;
            }

            technique T
            {
                pass P { PixelShader = compile ps_4_0 MainPS(); }
            }
            """;

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            "': COLOR0' on a pixel-shader OUTPUT STRUCT field is the shape FxPreParser cannot rewrite " +
            "(the struct may be a VS output, where COLOR is legal), so the vkd3d backcompat option is " +
            "the only thing that makes it compile. Errors: " +
            (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
        result.Value.Data.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// A whole legacy VS+PS effect (struct <c>POSITION</c>/<c>COLOR0</c> interstage, legacy
    /// <c>) : COLOR0</c> pixel return) on the SM4+ target.
    ///
    /// <para><b>Measured caveat, so nobody reads more into this than it proves:</b> unlike the
    /// two tests above, this one still passes with the backcompat option forced off, so it does
    /// NOT pin the option. vkd3d accepts <c>POSITION</c> on a vertex-shader output struct on its
    /// own, and the pixel return semantic is handled earlier by <c>FxPreParser</c>. It is kept as
    /// coverage for the ordinary MonoGame legacy-effect shape end to end on DirectX 11, which is
    /// worth having, not as a guard on the option.</para>
    /// </summary>
    [FnaFact]
    public async Task LegacyVsPsEffect_WithStructInterstageSemantics_CompilesForDirectX11()
    {
        using var cts = new CancellationTokenSource(CompileTimeout);

        const string source = """
            float4x4 WorldViewProjection;

            struct VSOutput
            {
                float4 Position : POSITION;
                float4 Color    : COLOR0;
            };

            VSOutput MainVS(float4 pos : POSITION, float4 color : COLOR0)
            {
                VSOutput output;
                output.Position = mul(pos, WorldViewProjection);
                output.Color = color;
                return output;
            }

            float4 MainPS(VSOutput input) : COLOR0
            {
                return input.Color;
            }

            technique T
            {
                pass P
                {
                    VertexShader = compile vs_4_0 MainVS();
                    PixelShader  = compile ps_4_0 MainPS();
                }
            }
            """;

        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = PlatformTarget.DirectX,
        }, cts.Token);

        result.IsSuccess.ShouldBeTrue(
            "an ordinary legacy MonoGame VS+PS effect must compile for the SM4+ DirectX target. Errors: " +
            (result.IsFailure ? string.Join(" | ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "<none>"));
        result.Value.Data.Length.ShouldBeGreaterThan(0);
    }
}
