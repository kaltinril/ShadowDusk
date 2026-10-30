sampler2D A; sampler2D B; float4 K;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 a = tex2D(A, uv * K.xy + K.zw); float s = 0; for (int i=0;i<4;i++) s += sin(a.x*i); float4 b = tex2D(B, uv); return s * b; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
