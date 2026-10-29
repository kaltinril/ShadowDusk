Texture2D Arr[2]; SamplerState S; sampler2D A;
float4 P(float2 uv : TEXCOORD0) : COLOR0 { return Arr[0].Sample(S, uv) + Arr[1].Sample(S, uv*2) + tex2D(A, uv); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
