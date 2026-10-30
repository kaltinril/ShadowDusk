sampler2D A; sampler2D B; float k;
float Zero() { return 0; }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; for (int i = 0; i < 2; i++) { if (Zero() > 0) c += tex2D(B, uv); c += tex2D(A, uv); } return c + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
