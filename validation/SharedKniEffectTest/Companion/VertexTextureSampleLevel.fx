// KniEffectTest companion (ShadowDusk-authored): vertex texture fetch, modern spelling.
// The vertex shader reads Tex at (0.5, 0.5) with SampleLevel and passes it on as the colour;
// Tex is a 1x1 green texture, so the pixel must be green.
#if defined(OPENGL)
#define VS_PROFILE vs_3_0
#define PS_PROFILE ps_3_0
#elif defined(SM6)
#define VS_PROFILE vs_6_0
#define PS_PROFILE ps_6_0
#else
#define VS_PROFILE vs_4_0
#define PS_PROFILE ps_4_0
#endif

float4x4 MatrixTransform;
Texture2D Tex;
SamplerState TexSampler;

struct VIn  { float4 Pos : POSITION0; float4 Col : COLOR0; float2 UV : TEXCOORD0; };
struct VOut { float4 Pos : SV_POSITION; float4 Col : COLOR0; };

VOut MainVS(VIn i)
{
    VOut o;
    o.Pos = mul(i.Pos, MatrixTransform);
    o.Col = Tex.SampleLevel(TexSampler, float2(0.5, 0.5), 0);
    return o;
}

float4 MainPS(VOut i) : SV_Target0 { return i.Col; }

technique T { pass P { VertexShader = compile VS_PROFILE MainVS(); PixelShader = compile PS_PROFILE MainPS(); } }
