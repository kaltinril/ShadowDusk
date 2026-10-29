sampler2D A; sampler2D B;
float k;
float4 H(float2 uv) { float4 c = 0; float a = 1, k = -1; if (k > 0) c = tex2D(B, uv); return c; }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = H(uv); return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
