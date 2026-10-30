// Matrix-interpolant packing: a helper declares a LOCAL typedef with the file-scope row_major typedef's name.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct VSIn { float4 Position : POSITION0; float3 Normal : NORMAL0; float3 Binormal : BINORMAL0; float3 Tangent : TANGENT0; };
typedef row_major float3x3 M33;
float3 Helper(float3 v) { typedef float3x3 M33; M33 t = float3x3(v, v, v); return t[0]; }
void VS(VSIn i, out float4 pos : POSITION0, out M33 m : TEXCOORD1) { pos = i.Position; m = float3x3(i.Tangent, i.Binormal, i.Normal); }
float4 PS(in M33 m : TEXCOORD1) : COLOR0 { return float4(m[1] + Helper(0) * 0, 1); }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
