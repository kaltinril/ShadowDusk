#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#if VULKAN
		#define PS_SHADERMODEL ps_6_0
	#else
		#define PS_SHADERMODEL ps_4_0_level_9_1
	#endif
#endif

#if VULKAN
Texture2D s0Texture;
SamplerState s0;
#else
sampler s0;
#endif

float _attenuation; // 800.0
float _linesFactor; // 0.04

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

#if VULKAN
float4 mainPS(VertexShaderOutput input) : SV_Target
{
	float4 color = s0Texture.Sample(s0, input.TexCoord);
	float scanline = sin(input.TexCoord.y * _linesFactor) * _attenuation;
	color.rgb -= scanline;
	return color;
}
#else
float4 mainPS(VertexShaderOutput input) : COLOR
{
	float4 color = tex2D(s0, input.TexCoord);
	float scanline = sin(input.TexCoord.y * _linesFactor) * _attenuation;
	color.rgb -= scanline;
	return color;
}
#endif

technique Scanlines
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL mainPS();
	}
}
