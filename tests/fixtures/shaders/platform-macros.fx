float4x4 WorldViewProj;

struct VSInput  { float4 Position : POSITION; };
struct VSOutput { float4 Position : SV_POSITION; };

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(input.Position, WorldViewProj);
    return output;
}

float4 PS(VSOutput input) : SV_TARGET
{
#if GLSL
    // Coordinate-flip for OpenGL NDC
    return float4(0.0, 1.0, 0.0, 1.0);
#elif SM4
    return float4(1.0, 0.0, 0.0, 1.0);
#else
    return float4(0.5, 0.5, 0.5, 1.0);
#endif
}

technique PlatformMacros
{
    pass Pass0
    {
        VertexShader = compile vs_5_0 VS();
        PixelShader  = compile ps_5_0 PS();
    }
}
