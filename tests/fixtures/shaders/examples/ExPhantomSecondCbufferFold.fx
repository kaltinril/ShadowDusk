// =============================================================================
// ExPhantomSecondCbufferFold.fx  —  ShadowDusk fresh regression fixture (#187)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #187).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the MIXED-FOLD member of the issue-#187 phantom class: one
//              cbuffer stays live (LiveTint is read normally, so the rewriter
//              emits `uniform vec4 ps_uniforms_vec4[1];` for it) while a SECOND
//              cbuffer's only member is read solely through an identity DXC's
//              -spirv backend cancels, dropping that whole cbuffer from the
//              SPIR-V. Synthesis must APPEND the phantom after the live
//              registers and RESIZE the existing declaration ([1] -> [2]) —
//              the resize branch of PatchGlUniformArrayDeclaration, unreachable
//              from any fully-folded fixture.
// Exercises  : two explicit cbuffers, one live + one fully folded away.
// Regression : Issue #187 (phantom-parameter backing, declaration resize).
// =============================================================================
#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

cbuffer LiveParams
{
    float4 LiveTint;
};

cbuffer GhostParams
{
    float3 GhostOffset;
};

struct PixelInput
{
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(PixelInput input) : COLOR0
{
    // GhostOffset's only reads cancel on the -spirv leg (its whole cbuffer is
    // removed from the SPIR-V); LiveTint stays live, keeping a real uniform
    // block — and an existing ps_uniforms_vec4 declaration — in the GLSL.
    float2 uv = (input.TexCoord * GhostOffset.xy) / GhostOffset.xy;
    return float4(uv, 0.0, 1.0) * LiveTint;
}

technique PhantomSecondCbuffer
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
