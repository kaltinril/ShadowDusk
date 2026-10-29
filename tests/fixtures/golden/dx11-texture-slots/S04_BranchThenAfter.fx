sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 r = 0; if (uv.x > 0.5) r = tex2D(A, uv); return r + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
