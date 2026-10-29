sampler2D A; sampler2D B;
static float4 G[2] = { tex2D(B, float2(0.5, 0.5)), float4(0, 0, 0, 0) };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return G[1] + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
