sampler2D A; sampler2D B; sampler2D C;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(A, uv); float4 b = tex2D(B, uv*2); float4 c = tex2D(C, uv*3); return c.x * b + a.y; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
