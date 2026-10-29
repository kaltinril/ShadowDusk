sampler2D A; sampler2D B; sampler2D C; float k; bool bk;
struct S { float4 Get(float2 uv) { return tex2D(B, uv); } };
float4 P(float2 uv : TEXCOORD0) : COLOR0 { S s; float4 b = s.Get(uv); return b + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
