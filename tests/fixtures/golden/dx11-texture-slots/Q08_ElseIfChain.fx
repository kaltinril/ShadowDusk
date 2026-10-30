sampler2D A; sampler2D B; sampler2D C;
float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; if (k > 0) c = tex2D(C, uv); else if (k < -1) c = tex2D(B, uv); else c = tex2D(A, uv); return c + tex2D(A, uv*3) + tex2D(B, uv * 2) + tex2D(C, uv*4); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
