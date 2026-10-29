// Render fixture: a float3x3 interpolant (the tangent-to-world basis of a normal-mapped
// shader) built from TANGENT / BINORMAL / NORMAL and read back in the pixel shader. `Mode`
// picks what the pixel shader shows: the row combination mul(v, Frame), the column
// combination mul(Frame, v) (a transposed packing swaps these two), or the TEXCOORD5
// interpolant declared right after the matrix's TEXCOORD2..4 registers.
float Mode;

struct VSIn
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float3 Binormal : BINORMAL0;
    float3 Tangent  : TANGENT0;
};

struct VSOut
{
    float4 Position : POSITION0;
    float2 Uv       : TEXCOORD0;
    float3x3 Frame  : TEXCOORD2;
    float4 After    : TEXCOORD5;
};

VSOut VS(VSIn i)
{
    VSOut o;
    o.Position = i.Position;
    o.Uv = float2(0.25, 0.75);
    o.Frame = float3x3(i.Tangent, i.Binormal, i.Normal);
    o.After = float4(i.Normal.zyx, 1);
    return o;
}

float4 PS(VSOut i) : COLOR0
{
    float3 v = float3(0.5, 0.25, 0.125);
    if (Mode < 0.5) return float4(mul(v, i.Frame), 1);
    if (Mode < 1.5) return float4(mul(i.Frame, v), 1);
    return i.After + float4(i.Uv, 0, 0) * 0.0625;
}

technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
