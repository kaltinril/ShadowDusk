#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#if VULKAN
		#define VS_SHADERMODEL vs_6_0
		#define PS_SHADERMODEL ps_6_0
	#else
		#define VS_SHADERMODEL vs_4_0_level_9_1
		#define PS_SHADERMODEL ps_4_0_level_9_1
	#endif
#endif

Texture2D SpriteTexture;

#if VULKAN
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



#if VULKAN
float4 MainPS(VertexShaderOutput input) : SV_Target
{
    float4 col = SpriteTexture.Sample(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    col.rgb = (col.r + col.g + col.b) / 3.0f;
    return col;

}
#else
float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 col = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    col.rgb = (col.r + col.g + col.b) / 3.0f;
    return col;

}
#endif

technique BasicColorDrawing
{
	pass P0
	{
	
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};