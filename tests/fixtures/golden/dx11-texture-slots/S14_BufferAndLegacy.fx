Buffer<float4> Buf : register(t0); sampler2D A;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Buf.Load(int(uv.x*4)) + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
