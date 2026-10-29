Texture2D Texture0;
Texture2D Texture1;
sampler2D Sampler0 = sampler_state { Texture = (Texture0); };
sampler2D Sampler1 = sampler_state { Texture = (Texture1); };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(Sampler1, uv) * 0.5 + tex2D(Sampler0, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
