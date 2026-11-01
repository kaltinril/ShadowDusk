texture TexA; texture TexB;
sampler SA = sampler_state { Texture = <TexA>; AddressU = Clamp; };
sampler SB = sampler_state { Texture = <TexB>; AddressU = Wrap; };
float4 F(sampler s, float2 uv) { return tex2D(s, uv); }
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return F(SB, uv) * 0.5 + F(SA, uv) + tex2D(SB, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 PS(); } }
