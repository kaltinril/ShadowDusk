sampler2D A; sampler2D B;
struct S { float4 Get(float2 uv) { return tex2D(B, uv); } };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { S s; return tex2D(A, uv) + s.Get(uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
