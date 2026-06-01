#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

float _attenuation; // 800.0
float _linesFactor; // 0.04

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 mainPS(VertexShaderOutput input) : COLOR
{
	float4 color = tex2D(s0, input.TexCoord);
	float scanline = sin(input.TexCoord.y * _linesFactor) * _attenuation;
	color.rgb -= scanline;
	return color;
}

technique Scanlines
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL mainPS();
	}
}
