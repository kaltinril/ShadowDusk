sampler2D A : register(s0);
sampler2D B : register(s1);
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) * 0.5 + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
