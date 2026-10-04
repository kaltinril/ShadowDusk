// StateBlockNoTexture.fx: legacy-sampler effect-parameter NAME fixture (see README.md in this folder).
// Shape: a `sampler_state` block that names no texture
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#elif SM6
	#define PS_SHADERMODEL ps_6_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D A = sampler_state
{
	MinFilter = Point;
	MagFilter = Point;
};

float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
	return tex2D(A, uv) * color;
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
