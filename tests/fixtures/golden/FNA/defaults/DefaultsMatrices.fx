// Initializer defaults: non-symmetric float2x2, float3x3, float4x4 and row_major float4x4 (a transpose is visible).
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float2x2 M2 = float2x2(0.1, 0.2, 0.3, 0.4);
float3x3 M3 = float3x3(0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9);
float4x4 M4 = float4x4(0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07, 0.08, 0.09, 0.10, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16);
row_major float4x4 M4r = float4x4(0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07, 0.08, 0.09, 0.10, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16);

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
	// Row vector times matrix with distinct weights: a transposed or zeroed default
	// changes every channel. Every column of every matrix is read, so the compiler
	// keeps the whole constant register range. The weights carry k (the vertex colour,
	// 1.0 at runtime) so the products are not uniform-only: fxc would otherwise fold
	// them into a preshader, a different code path from the one under test.
	float k = color.x;
	float2 a = mul(float2(0.5, 0.25) * k, M2);
	float3 b = mul(float3(0.5, 0.25, 0.125) * k, M3);
	float4 c = mul(float4(0.5, 0.25, 0.125, 0.0625) * k, M4);
	float4 d = mul(float4(0.5, 0.25, 0.125, 0.0625) * k, M4r);
	float s = dot(c, float4(1, 2, 3, 4));
	float t = dot(d, float4(4, 3, 2, 1));
	return float4(a.x + b.x, a.y + b.y, b.z + 0.05 * s + 0.05 * t, 1);
}

technique SpriteDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
