/* it's "quoted */
sampler2D SB;
sampler2D SA;
// Texture2D SA_Texture;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SA, uv) * 0.5 + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
