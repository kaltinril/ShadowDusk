sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return (tex2D(SC, uv).r > 0.5 || tex2D(SB, uv).r > 0.5) ? 1 : 0; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
