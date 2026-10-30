sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { Tb.Sample(SB, uv); float4 c = tex2D(A, uv); return c + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
