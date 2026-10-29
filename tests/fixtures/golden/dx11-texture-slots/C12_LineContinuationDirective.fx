#define LONGMACRO(x) \
   (x * 0.5)
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return LONGMACRO(tex2D(SA, uv)) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
