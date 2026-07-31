// =============================================================================
// ExProfileSm3BothArms.fx  —  ShadowDusk regression fixture (Phase 51 A10)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 51 A10).
//              Project-owned (same license as the repository).
// Purpose    : The MACRO half of the SD0015 reject set, and a verbatim capture of
//              the bug A10 was filed for. The header LOOKS like the standard
//              MonoGame cross-platform shim but names SM3 in BOTH arms, so the
//              DirectX branch asks for vs_3_0/ps_3_0 — which mgfxc refuses:
//                  Invalid profile 'vs_3_0'. Vertex shader 'VSMain' must be
//                  SM 4.0 level 9.1 or higher!
//              This is exactly what ShaderToyConverter emitted before A10; every
//              converted ShaderToy shader was therefore un-buildable by the
//              reference compiler for DirectX while compiling fine here.
//              The compile target is a MACRO NAME, so this exercises the
//              expansion path of the floor check (the literal path is covered by
//              ExProfileSm3OnDirectX.fx).
// Expect     : REJECT SD0015 on DirectX_11. COMPILE SUCCESS on OpenGL.
// =============================================================================
#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#endif

float4 Tint;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_Position; float2 UV : TEXCOORD0; };

VSOutput VSMain(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    output.UV = float2(input.Position.x * 0.5 + 0.5, 0.5 - input.Position.y * 0.5);
    return output;
}

float4 MainPS(VSOutput input) : COLOR0
{
    return float4(input.UV, 0.0, 1.0) * Tint;
}

technique BothArmsSm3
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL VSMain();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
};
