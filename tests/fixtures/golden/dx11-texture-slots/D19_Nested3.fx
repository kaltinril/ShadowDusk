sampler2D A; sampler2D B; sampler2D C;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, tex2D(B, uv).xy + tex2D(C, uv).xy); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
