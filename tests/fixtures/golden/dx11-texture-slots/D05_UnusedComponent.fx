sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(A, uv); float4 b = tex2D(B, uv); float4 r = float4(a.x, b.y, 0, 1); return float4(r.y, r.y, r.y, 1); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
