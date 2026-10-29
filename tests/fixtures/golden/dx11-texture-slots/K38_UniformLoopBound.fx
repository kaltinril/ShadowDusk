sampler2D A; sampler2D B; float k;
int Count;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; for (int i = 0; i < Count; i++) { c += tex2D(B, uv); } return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
