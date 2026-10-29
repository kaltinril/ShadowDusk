sampler2D A; sampler2D B; sampler2D C; float k; bool bk;
Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; Tb.SampleLevel(SB, uv, 0); return c + tex2D(A, uv) + tex2D(SB, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
