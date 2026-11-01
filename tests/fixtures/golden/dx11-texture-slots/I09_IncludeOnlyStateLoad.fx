#include "declState.fxh"
#define RD(s, c) tex2D(s, c)
sampler2D A;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Tb.Load(int3(0, 0, 0)) + RD(A, uv) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
