sampler2D A; sampler2D B; sampler2D C;
int mode; float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; switch (mode) { case 1: c = tex2D(A, uv); break; default: c = tex2D(B, uv); break; case 2: c = tex2D(C, uv); break; } return c; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
