// Matrix-interpolant packing: a helper declares a LOCAL struct with the file-scope struct's name and a column_major matrix; the entry points use the file-scope row_major one.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; row_major float3x3 M : TEXCOORD1; };
struct PSIn { float4 Position : POSITION0; float3x3 M : TEXCOORD1; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.M = float3x3(i.Tangent, i.Binormal, i.Normal); return o; }
float3 Helper(float3 v) { struct VSOut { float4 Position : POSITION0; float3x3 M : TEXCOORD1; }; VSOut t; t.Position = 0; t.M = float3x3(v, v, v); return t.M[0]; }
float4 PS(PSIn i) : COLOR0 { return float4(i.M[1] + Helper(0) * 0, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
