sampler2D A;
sampler2D B;
float k;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 b = tex2D(B, uv); float4 a = tex2D(A, uv); if (k > 0) return a; return b; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
