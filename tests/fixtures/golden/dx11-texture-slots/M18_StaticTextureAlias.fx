sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
static Texture2D Tc = Tb;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Tc.Load(int3(0, 0, 0)) + tex2D(A, uv) + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
