sampler2D A; sampler2D B; sampler2D C; float k; bool bk;
float4 F() { return tex2D(B, float2(0.5, 0.5)); }
static float4 G = F();
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return G + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
