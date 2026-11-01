sampler2D A;
#include "declState.fxh"
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) + Tb.Load(int3(0, 0, 0)) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
