sampler2D A; texture2D Tb, Tz; sampler SB = sampler_state { Texture = (Tb); };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(SB, uv) + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
