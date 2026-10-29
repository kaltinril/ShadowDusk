sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { if (tex2D(SC, uv).r >= tex2D(SB, uv).g) return 1; return 0; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
