// ModernBeforeLegacyRegister0.fx: legacy/modern sampler UNIT and SLOT fixture (see README.md in this folder).
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif
struct VSO { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TexCoord : TEXCOORD0; };

Texture2D T;
SamplerState S;
sampler2D A : register(s0);

float4 MainPS(VSO input) : COLOR0
{
	float2 uv = input.TexCoord;
	return tex2D(A, uv) * T.Sample(S, uv);
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
