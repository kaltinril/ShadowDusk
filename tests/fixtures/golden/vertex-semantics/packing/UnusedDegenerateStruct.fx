// Matrix-interpolant packing: an UNUSED struct holds a float1x3 at TEXCOORD1; the compiled entry points use a plain float4 there.
// The mgfxc 3.8.4.1 OpenGL golden beside this file is the reference.
struct Unused { float4 Position : POSITION0; float1x3 Weird : TEXCOORD1; };
struct VSOut { float4 Position : POSITION0; float4 Color : TEXCOORD1; };
VSOut VS(float4 p : POSITION0, float3 n : NORMAL0) { VSOut o; o.Position = p; o.Color = float4(n * 0.5 + 0.5, 1); return o; }
float4 PS(VSOut i) : COLOR0 { return i.Color; }
technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
