sampler2D A; sampler2D B; float k;
float4 FB(float2 uv) { return tex2D(B, uv); }
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = false ? FB(uv) : 0; return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
