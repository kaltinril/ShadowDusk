texture t;
sampler s0 = sampler_state { Texture = <t>; MipFilter = LINEAR; };
float4 PSMain(float2 uv : TEXCOORD0) : COLOR { return tex2D(s0, uv); }
technique T { pass P { PixelShader = compile ps_2_0 PSMain(); } }
