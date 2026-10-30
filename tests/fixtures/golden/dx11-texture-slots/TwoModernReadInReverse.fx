Texture2D T1;
Texture2D T2;
SamplerState SS;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return T2.Sample(SS, uv) * 0.5 + T1.Sample(SS, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
