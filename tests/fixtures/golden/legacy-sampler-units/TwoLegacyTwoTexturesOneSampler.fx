// TwoLegacyTwoTexturesOneSampler.fx: legacy/modern sampler UNIT and SLOT fixture (see README.md in this folder).
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif
struct VSO { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TexCoord : TEXCOORD0; };

sampler2D A;
sampler2D B;
Texture2D T;
Texture2D U;
SamplerState S;

float4 MainPS(VSO input) : COLOR0
{
	float2 uv = input.TexCoord;
	return tex2D(A, uv) * tex2D(B, uv) * T.Sample(S, uv) * U.Sample(S, uv);
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
