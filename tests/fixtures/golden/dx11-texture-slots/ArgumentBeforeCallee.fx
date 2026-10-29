sampler2D A;
sampler2D B;
float4 F(float4 c, float2 uv) { return c * tex2D(B, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return F(tex2D(A, uv), uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
