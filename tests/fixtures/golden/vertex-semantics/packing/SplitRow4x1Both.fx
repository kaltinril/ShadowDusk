// Matrix-interpolant packing: a row_major float4x1 interpolant in both stages: one register per row, one component each.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; row_major float4x1 W : TEXCOORD2; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.W = float4x1(i.Binormal.x, i.Tangent.z, i.Normal.y, i.Binormal.y); return o; }
float4 PS(VSOut i) : COLOR0 { return float4(i.W[0][0], i.W[1][0], i.W[2][0], i.W[3][0]); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
