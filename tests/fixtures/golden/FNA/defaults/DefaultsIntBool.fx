// Initializer defaults: int and bool scalars.
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

int Mode = 2;
bool Flag = true;
float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
	return float4(Mode / 4.0, Flag ? 1.0 : 0.0, 0, 1);
}

technique SpriteDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
