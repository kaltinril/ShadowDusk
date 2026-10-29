sampler2D A; sampler2D B;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return dot(tex2D(A, uv), float4(0,0,0,0)) + tex2D(B, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
