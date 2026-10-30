texture2D tex;
texture2D Tex;
sampler sa = sampler_state { Texture = <tex>; };
sampler sb = sampler_state { Texture = <Tex>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(sb, uv) * 0.5 + tex2D(sa, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
