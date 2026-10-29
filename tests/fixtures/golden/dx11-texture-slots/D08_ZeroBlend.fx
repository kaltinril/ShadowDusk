sampler2D A; sampler2D B; float W = 0;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) * W + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
