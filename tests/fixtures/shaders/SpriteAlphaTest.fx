#if OPENGL
	#define SV_POSITION POSITION
	#define PS_SHADERMODEL ps_3_0
#else
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;
float3 _alphaTest; // alphaCutoff, less than result, greater than result. defaults: 127, -1, 1


struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color : COLOR0;
	float2 TextureCoordinates : TEXCOORD0;
};


float4 MainPS( VertexShaderOutput input ) : COLOR
{
	float4 color = tex2D( s0, input.TextureCoordinates ) * input.Color;
	clip( ( color.a < _alphaTest.x ) ? _alphaTest.y : _alphaTest.z );

	return color;
}


technique SpriteDrawing
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL MainPS();
	}
};
