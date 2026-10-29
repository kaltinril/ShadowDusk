sampler2D A;
sampler2D B;
struct VO { float4 p : SV_Position; float2 uv : TEXCOORD0; };
VO V(float4 p : POSITION0, float2 uv : TEXCOORD0) { VO o; o.p = p; o.uv = uv; return o; }
float4 P(VO i) : COLOR0 { return tex2D(B, i.uv) * 0.5 + tex2D(A, i.uv); }
technique T { pass P0 { VertexShader = compile vs_4_0_level_9_1 V(); PixelShader = compile ps_4_0_level_9_1 P(); } }
