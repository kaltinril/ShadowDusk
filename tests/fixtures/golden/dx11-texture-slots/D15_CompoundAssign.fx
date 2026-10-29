sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 r = 1; r *= tex2D(B, uv); r = tex2D(A, uv) + r; return r; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
