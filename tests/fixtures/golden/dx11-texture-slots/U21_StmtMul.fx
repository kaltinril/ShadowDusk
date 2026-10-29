sampler2D A; sampler2D B; sampler2D C; float k; bool bk;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; tex2D(B, uv) * 2; return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
