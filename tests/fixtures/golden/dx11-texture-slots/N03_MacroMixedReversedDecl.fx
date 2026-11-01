sampler2D B; sampler2D A;
#define RD(s,c) tex2D(s,c)
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * RD(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
