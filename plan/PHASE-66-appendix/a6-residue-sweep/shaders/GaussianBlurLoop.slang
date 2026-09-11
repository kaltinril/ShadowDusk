// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/Nez/GaussianBlur.fx.
// Body copied as closely as possible; only the entry attribute and the legacy
// XNA-era `sampler`/`tex2D` uniform pair are kept UNCHANGED (that is the construct
// under test: a loose, un-cbuffer'd `sampler`, plus loose array uniforms, is the
// dominant declaration shape across the non-Gum, non-Slang fixture corpus and was
// never exercised by the shipped 21-shader real-slangc corpus, which only ever used
// modern Texture2D/SamplerState.Sample()).
// Exercises: bare `sampler` + `tex2D()`, loose (non-cbuffer) float2[]/float[] array
// uniforms, a literal-bounded for-loop.
sampler s0;

#define SAMPLE_COUNT 15

float2 _sampleOffsets[SAMPLE_COUNT];
float _sampleWeights[SAMPLE_COUNT];

[shader("fragment")]
float4 PixelShaderFunction(float2 texCoord : TEXCOORD0) : SV_Target
{
    float4 c = 0;

    for (int i = 0; i < SAMPLE_COUNT; i++)
        c += tex2D(s0, texCoord + _sampleOffsets[i]) * _sampleWeights[i];

    return c;
}
