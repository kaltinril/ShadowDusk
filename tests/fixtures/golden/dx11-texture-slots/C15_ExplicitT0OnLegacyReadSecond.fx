texture2D TexA : register(t0);
texture2D TexB : register(t1);
sampler SA = sampler_state { Texture = <TexA>; };
sampler SB = sampler_state { Texture = <TexB>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SB, uv) * 0.5 + tex2D(SA, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
