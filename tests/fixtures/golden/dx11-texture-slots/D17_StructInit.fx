sampler2D A; sampler2D B; struct S2 { float4 x; float4 y; };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { S2 s = { tex2D(B, uv), tex2D(A, uv) }; return s.y * s.x; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
