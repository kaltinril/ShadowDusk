sampler A : register(s1);
sampler B : register(s0);
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * 0.5 + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
