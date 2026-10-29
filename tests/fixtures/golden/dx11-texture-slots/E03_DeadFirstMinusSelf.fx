sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 b = tex2D(SB, uv); return (b - b) + tex2D(SC, uv) + tex2D(SB, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
