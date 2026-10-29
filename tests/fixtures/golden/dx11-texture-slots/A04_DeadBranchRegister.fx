#if SM6
Texture2D TA : register(t0); Texture2D TB : register(t1);
#else
Texture2D TA; Texture2D TB;
#endif
sampler A = sampler_state { Texture = <TA>; }; sampler B = sampler_state { Texture = <TB>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) + tex2D(A, uv*2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
