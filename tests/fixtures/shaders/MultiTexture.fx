#ifdef SHADOWDUSK_PC
#define PC_SAB(c) (float4(1, 1, 1, 2) - (c))
#define PC_ZERO(c) ((c) * 0)
#else
#define PC_SAB(c) (c)
#define PC_ZERO(c) (c)
#endif
#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

texture _secondTexture;
sampler2D _secondTextureSampler = sampler_state
{
	Texture = <_secondTexture>;
    AddressU = Clamp;
    AddressV = Clamp;
    MagFilter = Point;
    MinFilter = Point;
};

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 PixelShaderFunction(VertexShaderOutput input) : COLOR0
{
    float4 color  = tex2D(s0, input.TexCoord);
	float4 color2 = tex2D(_secondTextureSampler, input.TexCoord);

    return PC_SAB(color * color2);
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
