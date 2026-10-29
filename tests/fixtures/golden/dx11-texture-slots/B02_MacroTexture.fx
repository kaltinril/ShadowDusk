#define DECL(n) texture2D n##Tex; sampler n##Samp = sampler_state { Texture = <n##Tex>; };
DECL(A)
DECL(B)
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(BSamp, uv) * 0.5 + tex2D(ASamp, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
