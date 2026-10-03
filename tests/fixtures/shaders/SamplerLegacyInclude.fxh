//-----------------------------------------------------------------------------
// SamplerLegacyInclude.fxh — the header SamplerLegacyInclude.fx #includes
// (GitHub issue #308). Everything legacy lives HERE, out of the main file's
// sight: a bare `sampler` with a register, a `sampler2D` with a register and a
// `sampler_state` block (binding a Texture2D the main file declared before the
// #include), and a helper that reads one of them through `tex2D`.
//-----------------------------------------------------------------------------

sampler MaskA : register(s2);

sampler2D MaskB : register(s3) = sampler_state { Texture = <MaskBTexture>; };

float4 ReadMaskA(float2 texCoord)
{
	return tex2D(MaskA, texCoord);
}
