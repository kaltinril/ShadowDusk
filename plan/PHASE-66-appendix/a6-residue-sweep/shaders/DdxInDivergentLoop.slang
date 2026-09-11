// A6 residue sweep sample, derived from tests/fixtures/shaders/examples/Sd0400GradientInDivergentLoop.fx.
// Exercises: ddx() inside a for-loop whose body has a conditional `break` (the
// SD0400 portability-warning shape, issue #141) plus a legacy `sampler2D X =
// sampler_state { Texture = <tex>; };` + tex2D().
Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct PixelInput
{
    float4 Color : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(PixelInput input) : SV_Target
{
    float4 tex = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    float acc = 0.0;
    for (int i = 0; i < 8; i++)
    {
        if (acc > input.TexCoord.y * 4.0)
        {
            break;
        }
        acc += abs(ddx(input.TexCoord.x)) + 0.125;
    }

    return float4(tex.rgb * saturate(acc), tex.a);
}
