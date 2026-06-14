#nullable enable

using FluentAssertions;
using ShadowDusk.Compiler;
using ShadowDusk.Core;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Phase 44 — programmatic backing for the COMPILE-level cells of the
/// <c>docs/validation-matrix.md</c> tracker, so those claims cannot silently drift from
/// reality. Each case asserts that a representative shader either <b>compiles</b> for a target
/// or is <b>rejected with the documented <c>SD</c> code</b>. The differentiating claims pinned
/// here are exactly the ones that make the matrix non-trivial:
/// <list type="bullet">
///   <item>OpenGL and DirectX both compile a standard effect.</item>
///   <item>The MojoShader-runtime limits are <b>OpenGL-only</b>: vertex texture fetch and
///   <c>Texture2DArray</c> are rejected with <c>SD0210</c> on OpenGL but <b>compile on
///   DirectX</b> (the DXBC path has no such cap).</item>
///   <item>FNA (D3D9 fx_2_0) is Shader Model 2-3 only: an SM4 profile is rejected with
///   <c>SD0300</c>, while an SM3 shader compiles.</item>
/// </list>
/// Render-level matrix cells stay backed by the <c>validation/*</c> harnesses and the
/// <c>ImageTests</c> suite; this test pins only the compile/reject rung.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValidationMatrixCoverageTests
{
    public enum Outcome { Compiles, Rejected }

    // ---- Representative shaders (inline, minimal, self-contained) -----------------------

    // Standard VS+PS matrix transform — compiles on OpenGL and DirectX.
    private const string Standard = """
        matrix WVP;
        struct VIn  { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };
        VOut VS(VIn i){ VOut o=(VOut)0; o.Pos=mul(i.Pos,WVP); o.UV=i.UV; return o; }
        float4 PS(VOut i):SV_Target0{ return float4(i.UV,0,1); }
        technique T { pass P { VertexShader=compile vs_4_0 VS(); PixelShader=compile ps_4_0 PS(); } }
        """;

    // Vertex texture fetch — sampling a texture in the VS. GL runtime can't bind vertex
    // textures (SD0210); DirectX SM4/5 supports it.
    private const string VertexTextureFetch = """
        Texture2D HeightMap; SamplerState s;
        struct VIn  { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
        struct VOut { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };
        VOut VS(VIn i){ VOut o=(VOut)0; float h=HeightMap.SampleLevel(s,i.UV,0).r; i.Pos.y+=h; o.Pos=i.Pos; o.UV=i.UV; return o; }
        float4 PS(VOut i):SV_Target0{ return HeightMap.Sample(s,i.UV); }
        technique T { pass P { VertexShader=compile vs_4_0 VS(); PixelShader=compile ps_4_0 PS(); } }
        """;

    // Texture array — Texture2DArray. The MojoShader GL dialect doesn't model sampler2DArray
    // (SD0210); DirectX SM4/5 supports it.
    private const string TextureArray = """
        Texture2DArray Layers; SamplerState s;
        struct VIn  { float4 Pos : POSITION0; float3 UV : TEXCOORD0; };
        struct VOut { float4 Pos : SV_POSITION; float3 UV : TEXCOORD0; };
        VOut VS(VIn i){ VOut o=(VOut)0; o.Pos=i.Pos; o.UV=i.UV; return o; }
        float4 PS(VOut i):SV_Target0{ return Layers.Sample(s,i.UV); }
        technique T { pass P { VertexShader=compile vs_4_0 VS(); PixelShader=compile ps_4_0 PS(); } }
        """;

    // SM4 profile — FNA (D3D9 fx_2_0) rejects it loudly (SD0300).
    private const string Sm4 = """
        struct VIn  { float4 Pos : POSITION0; };
        struct VOut { float4 Pos : SV_POSITION; };
        VOut VS(VIn i){ VOut o=(VOut)0; o.Pos=i.Pos; return o; }
        float4 PS(VOut i):SV_Target0{ return 1; }
        technique T { pass P { VertexShader=compile vs_4_0 VS(); PixelShader=compile ps_4_0 PS(); } }
        """;

    // SM3 D3D9-style — compiles to the FNA fx_2_0 target.
    private const string Sm3Fna = """
        sampler2D Tex : register(s0);
        struct VIn  { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
        struct VOut { float4 Pos : POSITION0; float2 UV : TEXCOORD0; };
        VOut VS(VIn i){ VOut o=(VOut)0; o.Pos=i.Pos; o.UV=i.UV; return o; }
        float4 PS(VOut i):COLOR0{ return tex2D(Tex,i.UV); }
        technique T { pass P { VertexShader=compile vs_3_0 VS(); PixelShader=compile ps_3_0 PS(); } }
        """;

    public static IEnumerable<object?[]> Cases() => new[]
    {
        // shader, target, expected outcome, expected SD code (null when Compiles)
        new object?[] { "Standard",       Standard,           PlatformTarget.OpenGL,    Outcome.Compiles, null },
        new object?[] { "Standard",       Standard,           PlatformTarget.DirectX,   Outcome.Compiles, null },

        new object?[] { "VertexTexFetch", VertexTextureFetch, PlatformTarget.OpenGL,    Outcome.Rejected, "SD0210" },
        new object?[] { "VertexTexFetch", VertexTextureFetch, PlatformTarget.DirectX,   Outcome.Compiles, null },

        new object?[] { "TextureArray",   TextureArray,       PlatformTarget.OpenGL,    Outcome.Rejected, "SD0210" },
        new object?[] { "TextureArray",   TextureArray,       PlatformTarget.DirectX,   Outcome.Compiles, null },

        new object?[] { "SM4",            Sm4,                PlatformTarget.Fna,       Outcome.Rejected, "SD0300" },
        new object?[] { "SM3",            Sm3Fna,             PlatformTarget.Fna,       Outcome.Compiles, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task MatrixCell_CompilesOrRejectsAsDocumented(
        string shaderName, string source, PlatformTarget target, Outcome expected, string? expectedCode)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await new EffectCompiler().CompileAsync(source, new CompilerOptions
        {
            Target = target,
            SourceFileName = $"{shaderName}.fx",
        }, cts.Token);

        if (expected == Outcome.Compiles)
        {
            result.IsSuccess.Should().BeTrue(
                $"[{shaderName} -> {target}] must compile per the validation matrix; got: " +
                (result.IsFailure ? string.Join("; ", result.Error.Select(e => $"{e.Code}: {e.Message}")) : "ok"));
            result.Value.Data.Length.Should().BeGreaterThan(0);
        }
        else
        {
            result.IsFailure.Should().BeTrue(
                $"[{shaderName} -> {target}] must be rejected with {expectedCode} per the validation matrix, but it compiled");
            result.Error.Select(e => e.Code).Should().Contain(expectedCode,
                $"[{shaderName} -> {target}] the documented rejection code is {expectedCode}; got " +
                string.Join(", ", result.Error.Select(e => e.Code)));
        }
    }
}
