cbuffer Transforms
{
    float4x4 WorldViewProj;
    float4   DiffuseColor;
};

struct VertexInput  { float4 Position : POSITION; };
struct PixelInput   { float4 Position : SV_POSITION; };

PixelInput VS(VertexInput input)
{
    PixelInput o;
    o.Position = mul(input.Position, WorldViewProj);
    return o;
}

float4 PS(PixelInput input) : SV_TARGET { return DiffuseColor; }

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
