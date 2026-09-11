// A6 residue sweep sample, derived from tests/fixtures/shaders/ClipShader.fx.
// Exercises: a FOURTH legacy-sampler shape — modern `Texture2D` declared, but bound
// to a legacy `sampler X = sampler_state { Texture = <tex>; };` (mixing the modern
// texture-object declaration with the legacy sampler-binding syntax), `tex2D()`
// against that combined object, `all()` vector comparison, if/else with two return
// paths.
Texture2D ClipTexture;
sampler ClipTextureSampler = sampler_state { Texture = <ClipTexture>; };

Texture2D DrawTexture;
sampler DrawTexSampler = sampler_state { Texture = <DrawTexture>; };

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    float4 mask = tex2D(ClipTextureSampler, input.TextureCoordinates);
    float4 color = tex2D(DrawTexSampler, input.TextureCoordinates);

    if (all(mask) == all(float4(1, 1, 1, 1)))
    {
        return color;
    }
    else
    {
        return float4(0, 1, 1, 0);
    }
}
