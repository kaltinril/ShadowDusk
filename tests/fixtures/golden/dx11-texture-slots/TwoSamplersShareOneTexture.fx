texture Tex;
sampler A = sampler_state { Texture = <Tex>; AddressU = Clamp; };
sampler B = sampler_state { Texture = <Tex>; AddressU = Wrap; };
sampler2D C;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return tex2D(C, uv) + tex2D(B, uv) * 0.5 + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
