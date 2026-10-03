// Phase 59 §1, the second of the two requested effects: a handheld-LCD look. Luminance is
// quantized onto a palette ramp (Game Boy green at Saturation 0; a washed-out GBA look as
// Saturation rises), and a dot-matrix grid darkens the first row/column of every LCD cell.
// Pixel-only and reading only TEXCOORD0 and COLOR0, like CrtFilter.fx.
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

float4 ShadeDark;   // darkest palette entry
float4 ShadeLight;  // lightest palette entry
float  Levels;      // palette steps, e.g. 4
float  Saturation;  // 0 = palette only, 1 = source color
float2 Resolution;  // output size in pixels
float  CellSize;    // LCD cell size in pixels
float  GapDarken;   // 0..1

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
	float4 src = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
	float luma = dot(src.rgb, float3(0.299, 0.587, 0.114));
	float steps = max(Levels - 1.0, 1.0);
	float q = floor(luma * steps + 0.5) / steps;
	float3 palette = lerp(ShadeDark.rgb, ShadeLight.rgb, q);
	float3 color = lerp(palette, src.rgb, Saturation);

	float2 cell = frac(input.TextureCoordinates * Resolution / CellSize);
	float2 edge = step(cell, 1.0 / CellSize);
	color *= 1.0 - GapDarken * max(edge.x, edge.y);

	return float4(color, src.a);
}

technique RetroHandheld
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
