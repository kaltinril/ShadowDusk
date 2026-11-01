sampler2D A; sampler2D B;
#define RD(s,c) \
    tex2D(s,c)
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return RD(B, uv) * 0.5 + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
