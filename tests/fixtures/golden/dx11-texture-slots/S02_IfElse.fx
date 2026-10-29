sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 r; if (uv.x > 0.5) r = tex2D(A, uv); else r = tex2D(B, uv); return r; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
