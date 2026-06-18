// =============================================================================
// ExVsColorReturn.fx  —  ShadowDusk fresh example fixture (Phase 45, B6)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B6 — a VERTEX shader whose function-return semantic
//              is ': COLOR'. A VS that writes POSITION through an 'out' parameter
//              and returns a colour ('… , out float4 pos : POSITION) : COLOR') is
//              valid HLSL that fxc /T vs_3_0 and mgfxc accept (verified: fxc emits
//              code for it; mgfxc /Profile:OpenGL compiles it). At the token level
//              it ends '… ) : COLOR {', the same shape the PS COLOR->SV_Target
//              rewrite keys on, so the rewrite wrongly turned the VS return into the
//              PS-only ': SV_Target'. The fix DEFERS the COLOR-return rewrite and
//              applies it to every candidate EXCEPT functions named by a
//              'compile vs_* <name>' pass statement, so a VS entry keeps ': COLOR'
//              while the PS entry below is still rewritten.
// Exercises  : a VS that outputs POSITION via an 'out' param and returns ': COLOR',
//              passing the colour to the PS via the shared COLOR0 register; a PS
//              whose own ': COLOR' IS still rewritten (proving the deferral is
//              VS-only, not a blanket disable).
// Regression : Before the fix, the VS's ': COLOR' became ': SV_Target' (an invalid
//              VS output semantic) and the compile failed.
// Targets    : OpenGL + DirectX_11 (the RewriteToSm4 path where the bug lived). On
//              FNA (PreserveSm3) ': COLOR' is a valid SM3 output semantic that
//              passes through to vkd3d unchanged, so it was never affected there.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

matrix MatrixTransform;

// The B6 shape: a VS that writes POSITION via an 'out' param and RETURNS a colour.
// Token shape '… ) : COLOR {' is identical to a PS entry, but it must NOT be
// rewritten to ': SV_Target' because it is a vertex-shader entry.
float4 MainVS(float4 position : POSITION0,
              float4 color    : COLOR0,
              out float4 outPosition : SV_POSITION) : COLOR0
{
    outPosition = mul(position, MatrixTransform);
    return color;
}

// A normal pixel entry whose ': COLOR' DOES still get rewritten (proves the
// deferral skips only VS entries).
float4 MainPS(float4 color : COLOR0) : COLOR0
{
    return color;
}

technique VsColorReturnExample
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
