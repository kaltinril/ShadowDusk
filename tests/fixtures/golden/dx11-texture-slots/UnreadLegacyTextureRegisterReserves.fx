texture Unused : register(t0);
sampler2D A;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
