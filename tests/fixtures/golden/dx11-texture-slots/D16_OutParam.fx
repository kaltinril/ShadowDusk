sampler2D A; sampler2D B;
void F(out float4 x, float2 uv) { x = tex2D(B, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(A, uv * 2); float4 b; F(b, uv); return b * a; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
