sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 r = 0; [unroll] for (int i = 0; i < 2; i++) { if (tex2D(SC, uv).r < tex2D(SB, uv).r) r += 1; } return r; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
