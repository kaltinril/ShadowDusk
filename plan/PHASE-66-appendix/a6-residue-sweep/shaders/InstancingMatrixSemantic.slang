// A6 residue sweep sample, derived from tests/fixtures/shaders/third-party/MonoGame/Instancing.fx.
// Exercises: a VS with TWO parameters (the VSInput struct plus a second, bare
// `float4x4` parameter carrying its own semantic, BLENDWEIGHT — no other VS in the
// shipped corpus or the rest of this sweep takes more than one parameter), an
// `#if VULKAN` / `#else` branch selecting `transpose()` vs. not (the exact
// construct that motivated forwarding PlatformMacros to slangc, Phase 66 A6 — see
// SlangCompiler's own class doc comment), VS-only (no pixel entry point in the
// original; a trivial PS is added here only because SlangEntryScanner's synthesized
// technique needs at least one PS to be a useful smoke test, not because the
// construct under test needs one).
float4x4 View;
float4x4 Projection;

struct VSInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float2 TexCoord : TEXCOORD0;
    float4 Position : SV_Position;
};

struct PSInput
{
    float2 TexCoord : TEXCOORD0;
};

[shader("vertex")]
VSOutput VS(VSInput input, float4x4 worldTransposed : BLENDWEIGHT)
{
    VSOutput output = (VSOutput)0;

#if VULKAN
    float4x4 world = worldTransposed;
#else
    float4x4 world = transpose(worldTransposed);
#endif
    float4 positionWorld = mul(input.Position, world);
    float4 positionView = mul(positionWorld, View);
    output.Position = mul(positionView, Projection);

    output.TexCoord = input.TexCoord;

    return output;
}

[shader("fragment")]
float4 PS(PSInput input) : SV_Target
{
    return float4(input.TexCoord.xy, 0, 1);
}
