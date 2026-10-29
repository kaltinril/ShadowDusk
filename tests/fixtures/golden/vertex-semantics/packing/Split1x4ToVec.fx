// Matrix-interpolant packing: a float1x4 written by the VS, read by the PS as four float4 at TEXCOORD1..4: component k is register 1+k .x.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float1x4 W : TEXCOORD1; };
struct PSIn { float4 Position : POSITION0; float4 a : TEXCOORD1; float4 b : TEXCOORD2; float4 c : TEXCOORD3; float4 d : TEXCOORD4; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.W = float1x4(i.Tangent.x, i.Tangent.y, i.Binormal.z, i.Normal.x); return o; }
float4 PS(PSIn i) : COLOR0 { return float4(i.a.x, i.b.x, i.c.x, i.d.x); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
