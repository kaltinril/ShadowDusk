// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/Nez/HeatDistortion.fx.
// Exercises: a SECOND, distinct legacy declaration shape from GaussianBlurLoop/TwistBranch
// — `sampler2D X = sampler_state { Texture = <tex>; AddressU = Wrap; AddressV = Wrap; };`
// (an explicit address-mode sampler_state block, not just a bare `sampler`) alongside a
// bare `sampler s0;` for the second texture.
sampler s0;

texture _distortionTexture;
sampler2D _distortionTextureSampler = sampler_state
{
    Texture = <_distortionTexture>;
    AddressU = Wrap;
    AddressV = Wrap;
};

float _time;
float _distortionFactor;
float _riseFactor;

[shader("fragment")]
float4 mainPS(float2 coords : TEXCOORD0) : SV_Target
{
    float2 distortionUV = coords;
    distortionUV.y -= _time * -_riseFactor;

    float2 distortionMapValue = tex2D(_distortionTextureSampler, distortionUV).xy;

    distortionMapValue = ((distortionMapValue * 2.0) - 1.0);
    distortionMapValue *= _distortionFactor;
    distortionMapValue *= (coords.y);

    return tex2D(s0, distortionMapValue + coords);
}
