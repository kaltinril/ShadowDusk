// Initializer defaults: arrays of float, float2, float3, int, bool, int2, float2x2 and float4x4.
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float FA[4] = { 0.1, 0.2, 0.3, 0.4 };
float2 V2[3] = { float2(0.05, 0.10), float2(0.15, 0.20), float2(0.25, 0.30) };
float3 V3[2] = { float3(0.1, 0.2, 0.3), float3(0.4, 0.5, 0.6) };
int IA[3] = { 1, 2, 3 };
bool BA[3] = { true, false, true };
int2 I2[2] = { int2(1, 2), int2(3, 4) };
float2x2 MA2[2] = { float2x2(0.1, 0.2, 0.3, 0.4), float2x2(0.5, 0.6, 0.7, 0.8) };
float4x4 MA4[2] = { float4x4(0.01, 0.02, 0.03, 0.04, 0.05, 0.06, 0.07, 0.08, 0.09, 0.10, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16), float4x4(0.17, 0.18, 0.19, 0.20, 0.21, 0.22, 0.23, 0.24, 0.25, 0.26, 0.27, 0.28, 0.29, 0.30, 0.31, 0.32) };

float4 MainPS(float4 pos : SV_POSITION, float4 color : COLOR0, float2 tc : TEXCOORD0) : COLOR0
{
	// Every element of every array is read with a distinct weight. k (the vertex colour,
	// 1.0 at runtime) keeps the products out of fxc's uniform-only preshader folding.
	float k = color.x;
	float f = (FA[0] + 2 * FA[1] + 3 * FA[2] + 4 * FA[3]) * 0.1 * k;
	float2 v2 = (V2[0] + 2 * V2[1] + 3 * V2[2]) * k;
	float3 v3 = (V3[0] + 2 * V3[1]) * k;
	float i = ((IA[0] + 2 * IA[1] + 3 * IA[2]) * 0.02 + (I2[0].x + 2 * I2[0].y + 3 * I2[1].x + 4 * I2[1].y) * 0.01) * k;
	float b = ((BA[0] ? 0.1 : 0.0) + (BA[1] ? 0.2 : 0.0) + (BA[2] ? 0.4 : 0.0)) * k;
	float2 m2 = mul(float2(0.5, 0.25) * k, MA2[0]) + mul(float2(0.25, 0.5) * k, MA2[1]);
	float4 m4 = mul(float4(0.5, 0.25, 0.125, 0.0625) * k, MA4[0]) + mul(float4(0.0625, 0.125, 0.25, 0.5) * k, MA4[1]);
	float mm = dot(m4, float4(1, 2, 3, 4)) * 0.25;
	return float4(0.25 * (f + v2.x * 0.5 + v3.x * 0.25 + m2.x * 0.25), 0.25 * (i + v2.y * 0.25 + v3.y * 0.25 + b * 0.5 + m2.y * 0.25), 0.25 * (v3.z * 0.5 + mm), 1);
}

technique SpriteDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
