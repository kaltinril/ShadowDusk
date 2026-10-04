// GlMixedLegacyAfterModernRender.fx: legacy/modern sampler UNIT and SLOT fixture (see README.md in this folder).
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif
struct VSO { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TexCoord : TEXCOORD0; };

sampler2D MaskA;
Texture2D SpriteTexture;
SamplerState SpriteSampler;

float4 MainPS(VSO input) : COLOR0
{
	float2 uv = input.TexCoord;
	float4 t = SpriteTexture.Sample(SpriteSampler, uv);
	float4 a = tex2D(MaskA, uv);
	return float4(t.r, a.g, 0, 1);
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
