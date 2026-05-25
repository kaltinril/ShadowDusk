#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

texture _normalMap;
sampler _normalMapSampler = sampler_state { Texture = <_normalMap>; };

float _alphaCutoff;
float _alphaAsSelfIllumination;
float _selfIlluminationPower;


struct PixelMultiTextureOut
{
    float4 color  : COLOR0;
    float4 normal : COLOR1;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};


PixelMultiTextureOut deferredSpritePixel( VertexShaderOutput input )
{
    PixelMultiTextureOut output;

    // output our diffuse into the color texture
    output.color = tex2D( s0, input.TexCoord ) * input.Color;

    // get out of here if we fail the alpha test
	clip( ( output.color.a < _alphaCutoff ) ? -1 : 1 );


    // output our normal map into the normal texture. the alpha channel is used for self illumination with 1 being fully self illuminated
    // and 0 having no self illumination
    output.normal = tex2D( _normalMapSampler, input.TexCoord );
    output.normal.a *= _alphaAsSelfIllumination * _selfIlluminationPower;

    return output;
}


technique DeferredSpriteTechnique
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL deferredSpritePixel();
    }
}
