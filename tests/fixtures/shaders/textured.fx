Texture2D Texture;
SamplerState TextureSampler;

struct VertexInput  { float4 Position : POSITION; float2 TexCoord : TEXCOORD0; };
struct PixelInput   { float4 Position : SV_POSITION; float2 TexCoord : TEXCOORD0; };

PixelInput VS(VertexInput input)
{
    PixelInput o;
    o.Position = input.Position;
    o.TexCoord = input.TexCoord;
    return o;
}

float4 PS(PixelInput input) : SV_TARGET
{
    return Texture.Sample(TextureSampler, input.TexCoord);
}

technique Technique1
{
    pass Pass1
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS();
    }
}
