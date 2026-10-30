sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float2 c = uv; c = sin(c*3.1)*1.3 + cos(c.yx*2.7); c = sin(c*1.7) + cos(c*0.3); float4 a = tex2D(A, c); float4 b = tex2D(B, uv); return a * b; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
