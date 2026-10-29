// Matrix-interpolant packing: a row_major VS output read as a column_major PS input: each stage is laid out from its own declaration, as fxc does.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
float Mode;
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };

struct VSOut { float4 Position : POSITION0; row_major float3x3 Frame : TEXCOORD2; };
struct PSIn { float4 Position : POSITION0; float3x3 Frame : TEXCOORD2; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.Frame = float3x3(i.Tangent, i.Binormal, i.Normal); return o; }
float4 PS(PSIn i) : COLOR0 { if (Mode < 0.5) return float4(i.Frame[0],1); if (Mode < 1.5) return float4(i.Frame[1],1); return float4(i.Frame[2],1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
