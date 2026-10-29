Texture2D TA; SamplerState S; sampler2D B;
float4 F(Texture2D TA, float2 uv) { return TA.Sample(S, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) + F(TA, uv*2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
