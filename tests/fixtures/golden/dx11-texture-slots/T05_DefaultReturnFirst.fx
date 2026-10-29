sampler2D A; sampler2D B; sampler2D C;
int mode; float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { switch (mode) { default: return tex2D(A, uv); case 1: return tex2D(B, uv); } }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
