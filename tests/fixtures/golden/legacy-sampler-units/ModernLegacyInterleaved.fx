// ModernLegacyInterleaved.fx: legacy/modern sampler UNIT and SLOT fixture (see README.md in this folder).
#if OPENGL
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif
struct VSO { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 TexCoord : TEXCOORD0; };

Texture2D T;
SamplerState S;
sampler2D A;
Texture2D U;
SamplerState R;
sampler2D B;

float4 MainPS(VSO input) : COLOR0
{
	float2 uv = input.TexCoord;
	return tex2D(A, uv) * tex2D(B, uv) * T.Sample(S, uv) * U.Sample(R, uv);
}

technique Main
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
