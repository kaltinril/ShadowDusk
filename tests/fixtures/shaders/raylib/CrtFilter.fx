// Phase 59 §1, the first of the two requested effects: a fullscreen CRT pass (barrel
// curvature, RGB fringe, scanlines, vignette). Pixel-only and reading only TEXCOORD0 and
// COLOR0, so ONE source runs on MonoGame (SpriteBatch) and on raylib behind its built-in
// vertex shader (LoadShaderFromMemory(null, fs)).
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
	Texture = <SpriteTexture>;
};

float2 Resolution;        // output size in pixels
float  Curvature;         // 0 = flat screen
float  ScanlineIntensity; // 0..1
float  VignetteStrength;  // > 0
float  ChromaOffset;      // red/blue fringe, in pixels

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};

float2 Barrel(float2 uv)
{
	float2 c = uv * 2.0 - 1.0;
	c *= 1.0 + Curvature * dot(c, c);
	return c * 0.5 + 0.5;
}

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float2 uv = Barrel(input.TextureCoordinates);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
		return float4(0, 0, 0, 1);

	float2 shift = float2(ChromaOffset / Resolution.x, 0.0);
	float r = tex2D(SpriteTextureSampler, uv + shift).r;
	float g = tex2D(SpriteTextureSampler, uv).g;
	float b = tex2D(SpriteTextureSampler, uv - shift).b;
	float3 color = float3(r, g, b);

	float scan = 0.5 + 0.5 * cos(uv.y * Resolution.y * 3.14159265);
	color *= 1.0 - ScanlineIntensity * scan;

	float2 v = uv * (1.0 - uv);
	color *= pow(saturate(v.x * v.y * 16.0), VignetteStrength);

	return float4(color, 1.0) * input.Color;
}

technique Crt
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
