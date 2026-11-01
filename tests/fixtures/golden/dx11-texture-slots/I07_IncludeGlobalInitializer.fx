#include "declAB.fxh"
static float4 G = tex2D(B, float2(0.5, 0.5));
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return G + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
