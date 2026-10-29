sampler2D A; sampler2D B; sampler2D C; float k; bool bk;
Texture2D Tm; SamplerState SM;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; Tm.Sample(SM, tex2D(B, uv).xy); return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
