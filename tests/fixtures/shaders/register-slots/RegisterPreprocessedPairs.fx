//-----------------------------------------------------------------------------
// RegisterPreprocessedPairs.fx: pairings the raw text hides, seen only once the
// effect is preprocessed. Overlay is sampled through a macro sampler name
// (#define OVERLAY_SAMPLER), Tint1 through a token-pasting macro
// (SAMPLE_NAMED(Tint1) is Tint1.Sample(Tint1Sampler, uv)), and an inactive
// #if 0 branch samples SpriteTex through a different sampler.
//
// The Vulkan binding rewrite reads the raw text: it saw no pairing for Overlay
// or Tint1 and took the dead #if 0 call as SpriteTex's pairing, so each shipped a
// standalone SAMPLER descriptor next to a SAMPLED_IMAGE (a shape MonoGame's
// DesktopVK descriptor writer does not handle). Such an output now sends the
// effect through one more compile from its preprocessed source (macros
// expanded, #if 0 removed), where every pairing is literal.
//
// mgfxc 3.8.5 /Profile:Vulkan cannot build a working effect from this file (no
// explicit registers: its slots wrap to 224+). The committed golden
// tests/fixtures/golden/Vulkan/RegisterPreprocessedPairs.mgfx is mgfxc 3.8.5 on
// the expanded explicit-register form (SpriteTex t0/s0, Overlay t1/s1, Tint1
// t2/s2, the #if 0 branch dropped). Render row: validation/VsDrivenVulkan -- regslots.
//-----------------------------------------------------------------------------

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define PS_SHADERMODEL ps_6_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

#define OVERLAY_SAMPLER OverlaySampler
#define SAMPLE_NAMED(n) n.Sample(n##Sampler, uv)

Texture2D SpriteTex;
SamplerState SpriteSampler;
SamplerState UnusedSampler;
Texture2D Overlay;
SamplerState OverlaySampler;
Texture2D Tint1;
SamplerState Tint1Sampler;

float4 MainPS(float4 pos : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_Target0
{
#if 0
    return SpriteTex.Sample(UnusedSampler, uv);
#endif
    // A third each, so any texture left unbound shows as a darker picture.
    float4 c = SpriteTex.Sample(SpriteSampler, uv) + Overlay.Sample(OVERLAY_SAMPLER, uv) + SAMPLE_NAMED(Tint1);
    return c * color / 3.0;
}

technique T { pass P { PixelShader = compile PS_SHADERMODEL MainPS(); } }
