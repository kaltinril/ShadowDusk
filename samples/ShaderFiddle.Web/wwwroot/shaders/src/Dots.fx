#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

float angle; // 0.5
float scale; // 0.5
float2 ScreenSize;


float pattern( float angle, float2 uv, float scale )
{
   float s = sin( angle );
   float c = cos( angle );
   float2 tex = uv * ScreenSize;
   float2 pt = float2( c * tex.x - s * tex.y, s * tex.x + c * tex.y ) * scale;
   return ( sin( pt.x ) * sin( pt.y ) ) * 4.0;
}

struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 PixelShaderFunction(VertexShaderOutput input) : COLOR
{
    float4 color = tex2D( s0, input.TexCoord );
    float average = ( color.r + color.g + color.b ) / 3.0;
    float val = average * 10.0 - 5.0 + pattern( angle, input.TexCoord, scale );
    return float4( val, val, val, color.a );
}


technique Technique1
{
    pass Pass1
    {
        PixelShader = compile PS_SHADERMODEL PixelShaderFunction();
    }
}
