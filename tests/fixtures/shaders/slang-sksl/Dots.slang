// Slang twin of Dots.fx: arithmetic UV use, so the converter synthesizes ShadowDusk_Resolution.
Texture2D s0Texture;
SamplerState s0;
float angle;
float scale;
float2 ScreenSize;

float pattern(float angle, float2 uv, float scale)
{
    float s = sin(angle);
    float c = cos(angle);
    float2 tex = uv * ScreenSize;
    float2 pt = float2(c * tex.x - s * tex.y, s * tex.x + c * tex.y) * scale;
    return (sin(pt.x) * sin(pt.y)) * 4.0;
}

[shader("fragment")]
float4 PixelShaderFunction(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float4 color = s0Texture.Sample(s0, uv);
    float average = (color.r + color.g + color.b) / 3.0;
    float val = average * 10.0 - 5.0 + pattern(angle, uv, scale);
    return float4(val, val, val, color.a);
}
