// One vertex-input semantic at index 0 and at a non-zero index. The mgfxc 3.8.4.1 OpenGL golden
// beside this file pins the attribute records (usage + index) MonoGame's GL runtime binds by.
float4x4 WorldViewProj;

struct VSIn
{
    float4 Position : POSITION0;
    float4 A : BLENDWEIGHT0;
    float4 B : BLENDWEIGHT1;
};

struct VSOut
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
};

float4 Widen(float4 x) { return x; }

VSOut VS(VSIn i)
{
    VSOut o;
    o.Position = mul(i.Position, WorldViewProj);
    o.Color = Widen(i.A) + Widen(i.B) * 0.5;
    return o;
}

float4 PS(VSOut i) : COLOR0 { return i.Color; }

technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
