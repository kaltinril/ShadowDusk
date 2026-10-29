sampler2D SC;
sampler2D SB;
sampler2D SA;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float3 n = tex2D(SB, uv).rgb * 2 - 1; float l = saturate(dot(n, float3(0,0,1))); return float4(tex2D(SC, uv).rgb * l, tex2D(SA, uv).a); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_3 P(); } }
