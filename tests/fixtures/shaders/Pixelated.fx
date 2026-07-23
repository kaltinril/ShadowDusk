#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#if SM6
		#define VS_SHADERMODEL vs_6_0
		#define PS_SHADERMODEL ps_6_0
	#else
		#define VS_SHADERMODEL vs_4_0_level_9_1
		#define PS_SHADERMODEL ps_4_0_level_9_1
	#endif
#endif

Texture2D SpriteTexture;

#if SM6
SamplerState SpriteTextureSampler;
#else
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};
#endif



struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};



#if SM6
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    float pixels = 128.0f;  //todo set as param
    float pixelation = 4.0f;
    float mx = input.TextureCoordinates.x * pixels;
    float my = input.TextureCoordinates.y * pixels;

    float x = round(mx / pixelation) * pixelation;
    float y = round(my / pixelation) * pixelation;
    float2 coord = float2(x / pixels, y / pixels);

    return SpriteTexture.Sample(SpriteTextureSampler, coord);

}
#else
float4 MainPS(VertexShaderOutput input) : COLOR
{
    float pixels = 128.0f;  //todo set as param
    float pixelation = 4.0f;
    float mx = input.TextureCoordinates.x * pixels;
    float my = input.TextureCoordinates.y * pixels;

    float x = round(mx / pixelation) * pixelation;
    float y = round(my / pixelation) * pixelation;
    float2 coord = float2(x / pixels, y / pixels);

    return tex2D(SpriteTextureSampler, coord);

}
#endif

technique BasicColorDrawing
{
	pass P0
	{
	
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};