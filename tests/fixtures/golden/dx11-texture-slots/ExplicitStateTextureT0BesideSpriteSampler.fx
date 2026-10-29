texture Tex2 : register(t0);
sampler S0 : register(s0);
sampler S1 : register(s1) = sampler_state { Texture = <Tex2>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(S0, uv) * 0.5 + tex2D(S1, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
