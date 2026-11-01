sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
float4 L(Texture2D t) { return t.Load(int3(0, 0, 0)); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) + tex2D(SB, uv) + L(Tb); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
