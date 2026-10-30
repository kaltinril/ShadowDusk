sampler2D A; sampler2D B;
float k;

float4 P(float2 uv : TEXCOORD0) : COLOR0 { float4 c = 0; [loop] for (int i = 0; i < 4; i++) { if (k > 0) { c += tex2D(B, uv); } break; } return c + tex2D(A, uv) + tex2D(B, uv * 2); }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
