// Slang twin of SkslConverterTests.GradientFx: left-right lerp between two uniform colors.
float4 LeftColor;
float4 RightColor;

[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    return lerp(LeftColor, RightColor, uv.x);
}
