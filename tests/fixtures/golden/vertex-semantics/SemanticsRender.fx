// Render fixture for validation/VertexSemanticsGl and validation/VertexSemanticsKniGl.
// Twelve vertex inputs, one per semantic family (plus a non-zero index for TEXCOORD and
// BLENDWEIGHT). The vertex shader passes the attribute picked by `Pick` through as the
// colour, so each render shows exactly one attribute's data: a record bound to the wrong
// vertex element shows another attribute's colour, and an unbound one shows zero.
float Pick;

struct VSIn
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float3 Binormal : BINORMAL0;
    float3 Tangent  : TANGENT0;
    float4 Indices  : BLENDINDICES0;
    float4 Weights  : BLENDWEIGHT0;
    float  PSize    : PSIZE0;
    float  Fog      : FOG0;
    float  Depth    : DEPTH0;
    float2 Uv3      : TEXCOORD3;
    float4 Weights1 : BLENDWEIGHT1;
    float4 Color    : COLOR0;
};

struct VSOut
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
};

float4 Scalar(float s) { return float4(s, s * 0.5, 1 - s, 1); }

VSOut VS(VSIn i)
{
    VSOut o;
    o.Position = i.Position;
    float4 c = float4(0, 0, 0, 1);
    if      (Pick < 0.5)  c = float4(i.Normal, 1);
    else if (Pick < 1.5)  c = float4(i.Binormal, 1);
    else if (Pick < 2.5)  c = float4(i.Tangent, 1);
    else if (Pick < 3.5)  c = i.Indices / 8.0;
    else if (Pick < 4.5)  c = i.Weights;
    else if (Pick < 5.5)  c = Scalar(i.PSize);
    else if (Pick < 6.5)  c = Scalar(i.Fog);
    else if (Pick < 7.5)  c = Scalar(i.Depth);
    else if (Pick < 8.5)  c = float4(i.Uv3, 0.5, 1);
    else if (Pick < 9.5)  c = i.Weights1;
    else                  c = i.Color;
    o.Color = c;
    return o;
}

float4 PS(VSOut i) : COLOR0 { return i.Color; }

technique T { pass P { VertexShader = compile vs_3_0 VS(); PixelShader = compile ps_3_0 PS(); } }
