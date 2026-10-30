float4x4 M;
sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return mul(tex2D(SB, uv), M) + mul(tex2D(SC, uv), M); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
