#include "declAB.fxh"
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) * 0.5 + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
