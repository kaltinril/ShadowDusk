sampler2D s;
sampler2D S;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S, uv) * 0.5 + tex2D(s, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
