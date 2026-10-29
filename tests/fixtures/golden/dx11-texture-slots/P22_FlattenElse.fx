sampler2D A; sampler2D B;
float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; [flatten] if (k > 0) c = tex2D(B, uv); else c = tex2D(A, uv); return c; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
