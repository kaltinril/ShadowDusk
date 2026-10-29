sampler2D A; sampler2D B; static const bool UseA = false;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 r = tex2D(B, uv); if (UseA) r += tex2D(A, uv); return r; }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
