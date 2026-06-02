// ShadowDusk platform macros — DO NOT EDIT (generated)
#define MGFX 1
#define GLSL 1
#define OPENGL 1
#line 1 "C:/git/ShadowDusk/tests/fixtures/shaders/Invert.fx"
#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;

SamplerState SpriteTextureSampler;






struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};



float4 MainPS(VertexShaderOutput input) : SV_Target
{
    float4 col = SpriteTexture.Sample(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    col.rgb = 1 - col.rgb; // Invert color
    col.a = col.a;
    return col;

}

                           
 
        
  
 
                                                
  
 ;
