sampler2D A; sampler2D B; sampler2D C;

float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; for (int i = 0; i < 2; i++) { continue; c += tex2D(B, uv); } return c + tex2D(A, uv*3) + tex2D(B, uv * 2) + tex2D(C, uv*4); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
