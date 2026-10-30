sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return float4(reflect(tex2D(SC, uv).xyz, tex2D(SB, uv).xyz), 1); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
