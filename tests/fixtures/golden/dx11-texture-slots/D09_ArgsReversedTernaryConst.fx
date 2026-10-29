sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return 1 > 0 ? tex2D(B, uv) : tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
