sampler2D A; sampler2D B; sampler2D C;
int mode; float k;
static const int sm = 1;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; switch (sm) { default: c = tex2D(A, uv); break; case 1: c = tex2D(B, uv); break; } return c; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
