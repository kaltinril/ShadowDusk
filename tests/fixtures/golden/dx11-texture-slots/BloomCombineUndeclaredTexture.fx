sampler BloomSampler : register(s0);
sampler BaseSampler : register(s1) = sampler_state { Texture = (Tex2); };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(BloomSampler, uv) * 0.5 + tex2D(BaseSampler, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
