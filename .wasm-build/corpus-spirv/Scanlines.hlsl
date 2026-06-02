// ShadowDusk platform macros — DO NOT EDIT (generated)
#define MGFX 1
#define GLSL 1
#define OPENGL 1
#line 1 "C:/git/ShadowDusk/tests/fixtures/shaders/Scanlines.fx"
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D s0_SDTexture; SamplerState s0;

float _attenuation; // 800.0
float _linesFactor; // 0.04

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 mainPS(VertexShaderOutput input) : SV_Target
{
	float4 color = s0_SDTexture.Sample(s0, input.TexCoord);
	float scanline = sin(input.TexCoord.y * _linesFactor) * _attenuation;
	color.rgb -= scanline;
	return color;
}

                   
 
        
  
                                                
  
 

