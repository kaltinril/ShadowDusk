sampler2D A; Texture2D Tb; sampler SB = sampler_state { Texture = <Tb>; };
float4 P(float2 uv : TEXCOORD0, float4 pos : SV_Position) : COLOR0 { float4 l = Tb.Load(int3(pos.xy, 0)); float4 c = tex2D(A, uv); return l + c + tex2D(SB, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
