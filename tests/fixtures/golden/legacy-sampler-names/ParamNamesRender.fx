// ParamNamesRender.fx: legacy-sampler effect-parameter NAME fixture (see README.md in this folder).
// Shape: the rung-4 render arm (validation/SamplerRegisterOrderGl "param-names"): SpriteBatch's
// `SpriteSampler : register(s0)`, a bare `MaskA` and a texture-less `sampler_state` `MaskB`, the
// masks bound ONLY through effect.Parameters["MaskA"] / ["MaskB"], mgfxc's names.
// BLUE sprite, RED MaskA, GREEN MaskB, output (a.r, b.g, sprite.b, 1): white = correct.
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#elif SM6
	#define PS_SHADERMODEL ps_6_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler SpriteSampler : register(s0);
sampler2D MaskA;
sampler2D MaskB = sampler_state
{
	MinFilter = Point;
	MagFilter = Point;
};

float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
	float4 s = tex2D(SpriteSampler, uv);
	float4 a = tex2D(MaskA, uv);
	float4 b = tex2D(MaskB, uv);
	return float4(a.r, b.g, s.b, 1);
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
