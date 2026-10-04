// SpriteTextureSampler.fx: legacy-sampler effect-parameter NAME fixture (see README.md in this folder).
// Shape: the MonoGame template's `Texture2D SpriteTexture;` + `sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };`
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#elif SM6
	#define PS_SHADERMODEL ps_6_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
	return tex2D(SpriteTextureSampler, uv) * color;
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
