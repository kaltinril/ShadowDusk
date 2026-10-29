tbuffer TB { float4 k; };
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SA, uv) * k + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
