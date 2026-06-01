#if OPENGL
	#define SV_POSITION POSITION
	#define VS_SHADERMODEL vs_3_0
	#define PS_SHADERMODEL ps_3_0
#else
	#define VS_SHADERMODEL vs_4_0_level_9_1
	#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler s0;

texture _dissolveTex;
sampler _dissolveTexSampler = sampler_state { Texture = <_dissolveTex>; };

float _progress; // 0 - 1 where 0 is no change to s0 and 1 will discard all of s0 where _dissolveTex.r < value
float _dissolveThreshold; // 0.04
float4 _dissolveThresholdColor; // the color that will be used when _dissolveTex is between _progress +- _dissolveThreshold


struct VertexShaderOutput
{
	float4 Position : SV_POSITION;
	float4 Color    : COLOR0;
	float2 TexCoord : TEXCOORD0;
};

float4 mainPixel( VertexShaderOutput input ) : COLOR0
{
	float progress = _progress + _dissolveThreshold;

	float4 color = tex2D( s0, input.TexCoord );
	// get dissolve from 0 - 1 where 0 is pure white and 1 is pure black
	float dissolveAmount = 1 - tex2D( _dissolveTexSampler, input.TexCoord ).r;

	// when our dissolve.r (dissolveAmount) is less than progress we discard
	if( dissolveAmount < progress - _dissolveThreshold )
		discard;

	bool b = dissolveAmount < progress;
	float colorAmount = lerp( 1, 0, 1 - saturate( abs( progress - _dissolveThreshold - dissolveAmount ) / _dissolveThreshold ) );
	float4 thresholdColor = lerp( float4( 0, 0, 0, 1 ), _dissolveThresholdColor, colorAmount );
	return lerp( color, color * thresholdColor, b );
}


technique Dissolve
{
	pass P0
	{
		PixelShader = compile PS_SHADERMODEL mainPixel();
	}
};