sampler2D A;
sampler2D B;
sampler2D C;
float4 G(float2 uv) { return tex2D(C, uv); }
float4 F(float2 uv) { return tex2D(B, uv) + G(uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return F(uv) * tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
