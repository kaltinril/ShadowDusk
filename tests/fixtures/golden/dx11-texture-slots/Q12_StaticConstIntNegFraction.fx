sampler2D A; sampler2D B; sampler2D C;
static const int N = -0.5;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; for (int i = 0; i > N; i--) { c += tex2D(B, uv); } return c + tex2D(A, uv*3) + tex2D(B, uv * 2) + tex2D(C, uv*4); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
