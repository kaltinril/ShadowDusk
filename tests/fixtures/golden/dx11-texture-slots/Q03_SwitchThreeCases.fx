sampler2D A; sampler2D B; sampler2D C;
int mode;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; switch (mode) { case 2: c = tex2D(C, uv); break; case 0: c = tex2D(A, uv); break; case 1: c = tex2D(B, uv); break; } return c + tex2D(A, uv*3) + tex2D(B, uv * 2) + tex2D(C, uv*4); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
