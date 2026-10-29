sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return mad(tex2D(SC, uv), tex2D(SB, uv), 0.5); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
