// old: Texture2D TA : register(t3)
Texture2D TA; SamplerState S; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return TA.Sample(S, uv) + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
