// Phase 43 (F2) corpus fixture: annotations on a PARAMETER (mixed types), a
// TECHNIQUE, and a PASS. ShadowDusk-only — mgfxc 3.8.2's MGFX.tpg grammar has no
// annotation production for technique/pass blocks, so no mgfxc golden can exist;
// the bar for this fixture is loading in real MonoGame 3.8.2 Effect (the v10
// format stores only annotation counts, never bodies).
// TRUE SV_POSITION deliberately (no `#define SV_POSITION POSITION` alias) — see
// VsTransformColorTexture.fx: the alias produces a dead user-varying on the DXC path.
#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4 GlowColor < string UIName = "Glow Color"; int UIOrder = 2; bool UIHidden = false; float UIScale = 1.5; >;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = input.Position;
    return output;
}

float4 PS(VSOutput input) : COLOR { return GlowColor; }

technique Glow < string Group = "PostProcess"; >
{
    pass P0 < bool Skip = false; >
    {
        VertexShader = compile VS_SHADERMODEL VS();
        PixelShader  = compile PS_SHADERMODEL PS();
    }
}
