sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
static float4 G = Tb[int2(0, 0)];
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return G + tex2D(A, uv) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
