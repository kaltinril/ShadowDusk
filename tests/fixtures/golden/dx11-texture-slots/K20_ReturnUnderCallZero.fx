sampler2D A; sampler2D B; float k;
float Zero() { return 0; }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { if (Zero() > 0) return tex2D(A, uv); return tex2D(B, uv) + tex2D(A, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
