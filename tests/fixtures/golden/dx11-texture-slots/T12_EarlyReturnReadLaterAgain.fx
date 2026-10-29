sampler2D A; sampler2D B; sampler2D C;
int mode; float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { if (k > 0) return tex2D(B, uv); float4 c = tex2D(A, uv); return c + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
