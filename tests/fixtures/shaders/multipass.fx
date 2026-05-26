float4 PS_Solid(float4 pos : SV_POSITION) : SV_TARGET { return float4(1, 0, 0, 1); }
float4 PS_Alpha(float4 pos : SV_POSITION) : SV_TARGET { return float4(1, 0, 0, 0.5); }

float4 VS(float4 pos : POSITION) : SV_POSITION { return pos; }

technique Technique1
{
    pass Opaque
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS_Solid();
    }
    pass Transparent
    {
        VertexShader = compile vs_4_0 VS();
        PixelShader  = compile ps_4_0 PS_Alpha();
    }
}
