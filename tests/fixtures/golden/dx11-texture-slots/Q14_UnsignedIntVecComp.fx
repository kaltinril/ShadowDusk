sampler2D A; sampler2D B; sampler2D C;
unsigned int k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; if (-1 >= k) c = tex2D(B, uv); return c + tex2D(A, uv*3) + tex2D(B, uv * 2) + tex2D(C, uv*4); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
