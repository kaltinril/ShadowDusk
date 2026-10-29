Texture2D Tx; SamplerState S; sampler2D B; sampler A = sampler_state { Texture = <Tx>; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Tx.Sample(S, uv) + tex2D(B,uv) + tex2D(A, uv*2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
