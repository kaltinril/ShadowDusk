sampler2D A; sampler2D B;
float4 P1(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv) + tex2D(B, uv); }
float4 P2(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(B, uv) + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P1(); } pass P1 { PixelShader = compile ps_4_0_level_9_1 P2(); } }
