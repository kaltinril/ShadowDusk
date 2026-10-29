sampler2D SC;
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return (tex2D(SA, uv).r > tex2D(SC, uv).r) ? tex2D(SB, uv) : 0; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
