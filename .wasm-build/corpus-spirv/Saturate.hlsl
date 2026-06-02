// ShadowDusk platform macros — DO NOT EDIT (generated)
#define MGFX 1
#define GLSL 1
#define OPENGL 1
#line 1 "C:/git/ShadowDusk/tests/fixtures/shaders/Saturate.fx"
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

#define BLOOM_THRESHOLD 0.25
#define BLOOM_INTENSITY 2.0
#define BLOOM_SATURATION 0.8

float4 BloomThreshold;
float BloomIntensity;
float BloomSaturation;

Texture2D TextureSampler_SDTexture; SamplerState TextureSampler;

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 BloomPass(VertexShaderOutput input) : SV_Target
{
	float4 color = TextureSampler_SDTexture.Sample(TextureSampler, input.TexCoord);
	color = saturate(color - BloomThreshold) * BloomIntensity + color;
	color = saturate(color);
	color = lerp(color, color.rgba + color.rgba * BloomSaturation, BloomSaturation);
	return color;
}

               
 
           
  
                                                   
  
 

