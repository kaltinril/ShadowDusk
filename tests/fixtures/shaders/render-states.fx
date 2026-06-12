// Phase 43 (F1): pass render-state coverage fixture, mgfxc-compilable so a real
// mgfxc golden exists (tests/fixtures/golden/{OpenGL,DirectX_11}/render-states.mgfx).
// Keys are spelled in mgfxc 3.8.2's MGFX.tpg token set (ZEnable, not the
// DepthBufferEnable alias ShadowDusk also accepts), and profiles use the standard
// OPENGL macro so mgfxc's OpenGL target (SM3 ceiling) can build it.
// TRUE SV_POSITION deliberately (no `#define SV_POSITION POSITION` alias) — see
// VsTransformColorTexture.fx: the alias produces a dead user-varying on the DXC path.
#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : COLOR { return float4(1, 1, 1, 0.5); }

technique RenderStates
{
    pass Pass0
    {
        VertexShader        = compile VS_SHADERMODEL VS();
        PixelShader         = compile PS_SHADERMODEL PS();
        CullMode            = None;
        AlphaBlendEnable    = True;
        ZEnable             = False;
    }
}
