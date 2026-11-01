sampler2D A; sampler2D B; Texture2D M; SamplerState MS;
float4 F(float k, sampler2D s, float2 uv) { return tex2Dlod(s, float4(uv, 0, k)) + tex2D(s, uv); }
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return M.Sample(MS, uv) + F(2, B, uv) + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 PS(); } }
