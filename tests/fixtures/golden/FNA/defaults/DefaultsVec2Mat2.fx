// Initializer defaults: float2 vector plus a float2x2 matrix (0.5 * identity).
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float2 Offset = float2(0.25, 0.75);
float2x2 M = float2x2(0.5, 0.0, 0.0, 0.5);
float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
	float2 v = mul(Offset, M);
	return float4(v.x, v.y, 0, 1);
}

technique SpriteDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
