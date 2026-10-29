// KniEffectTest companion (ShadowDusk-authored): vertex texture fetch, XNA spelling (tex2Dlod on a
// legacy sampler_state). Same picture as VertexTextureSampleLevel.fx: the pixel must be green.
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
texture Tex;
sampler TexSampler = sampler_state { Texture = <Tex>; MinFilter = Point; MagFilter = Point; MipFilter = Point; };

struct VIn  { float4 Pos : POSITION0; float4 Col : COLOR0; float2 UV : TEXCOORD0; };
struct VOut { float4 Pos : POSITION0; float4 Col : COLOR0; };

VOut MainVS(VIn i)
{
    VOut o;
    o.Pos = mul(i.Pos, MatrixTransform);
    o.Col = tex2Dlod(TexSampler, float4(0.5, 0.5, 0, 0));
    return o;
}

float4 MainPS(VOut i) : COLOR0 { return i.Col; }

technique T { pass P { VertexShader = compile VS_PROFILE MainVS(); PixelShader = compile PS_PROFILE MainPS(); } }
