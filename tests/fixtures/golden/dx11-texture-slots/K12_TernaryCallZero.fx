sampler2D A; sampler2D B; float k;
float Zero() { return 0; }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = (Zero() > 0) ? tex2D(B, uv) : 0; return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
