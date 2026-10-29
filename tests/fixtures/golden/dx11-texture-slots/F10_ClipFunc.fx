sampler2D SC;
sampler2D SB;
sampler2D SA;
void K(float2 uv) { clip(tex2D(SA, uv).a - 0.1); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = tex2D(SC, uv); float4 b = tex2D(SB, uv); K(uv); return c + b; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
