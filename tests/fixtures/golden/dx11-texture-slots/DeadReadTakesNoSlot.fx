sampler2D A;
sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 d = tex2D(A, uv); d = 1; return d * tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
