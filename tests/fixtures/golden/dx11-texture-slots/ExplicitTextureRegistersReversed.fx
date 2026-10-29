texture2D Diffuse : register(t1);
sampler DiffuseSampler = sampler_state { Texture = (Diffuse); };
texture2D Spec : register(t0);
sampler SpecSampler = sampler_state { Texture = (Spec); };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(DiffuseSampler, uv) * 0.5 + tex2D(SpecSampler, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
