// Matrix-interpolant packing: a float1x4 interpolant declared the same way in both stages: fxc lays it out over four one-component registers.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
struct VSOut { float4 Position : POSITION0; float1x4 W : TEXCOORD1; };
VSOut VS(VSIn i) { VSOut o; o.Position = i.Position; o.W = float1x4(i.Tangent.x, i.Tangent.y, i.Binormal.z, i.Normal.x); return o; }
float4 PS(VSOut i) : COLOR0 { return float4(i.W[0][0], i.W[0][1], i.W[0][2], i.W[0][3]); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
