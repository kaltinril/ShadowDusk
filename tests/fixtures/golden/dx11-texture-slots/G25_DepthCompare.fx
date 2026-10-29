sampler2D SC;
sampler2D SB;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { float d = tex2D(SB, uv).r; return (d > tex2D(SC, uv).r) ? float4(1,0,0,1) : float4(0,1,0,1); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
