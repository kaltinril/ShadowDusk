// Pixel half of the Textured pair. VSOutput is declared here as well, identically to the vertex
// source, so the merge must write it once.
Texture2D SpriteTexture;
SamplerState SpriteSampler;
float4 Tint;

struct VSOutput
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD0;
};

float4 PSMain(VSOutput input) : SV_Target0
{
    return SpriteTexture.Sample(SpriteSampler, input.TexCoord) * Tint;
}
