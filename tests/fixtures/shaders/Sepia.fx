#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;
float3 _sepiaTone; // defaults to 1.2, 1.0, 0.8


struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};


float4 PixelShaderFunction( VertexShaderOutput input ) : COLOR0
{
    float4 tex = tex2D( s0, input.TexCoord );

    // first we need to convert to greyscale
    float grayScale = dot( tex.rgb, float3( 0.3, 0.59, 0.11 ) );

    tex.rgb = grayScale * _sepiaTone;

    return tex;
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
