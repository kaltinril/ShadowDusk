// Issue #229 - wave/quad-intrinsic render fixture for the Vulkan gate (validation/VsDrivenVulkan
// `wave` mode).
//
// VsTransformColorTexture's exact VS, parameters and registers, with a pixel shader that runs
// wave and quad intrinsics and still returns the SAME colour when they behave per spec:
//   * QuadReadAcrossX(SV_Position.x) is the horizontal quad neighbour's pixel centre, so it
//     must differ from this pixel's by exactly 1.0 (a driver that no-ops the read fails this);
//   * WaveActiveSum(1) and WaveActiveCountBits(true) both count the active lanes, so they
//     must agree;
//   * QuadReadAcrossX applied twice returns this lane's own value.
// Any failed check paints magenta. A correct run is therefore pixel-identical to the mgfxc
// golden of VsTransformColorTexture, which is what the gate diffs against: mgfxc itself cannot
// build wave ops for Vulkan, so this is the only reference-compiler oracle available.
//
// Every intrinsic is evaluated in uniform control flow (no early return before them), because
// quad and wave reads from a lane that already returned are undefined.
//
// The non-SM6 branch is VsTransformColorTexture verbatim: wave ops need Shader Model 6.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
float4   Tint;

#if SM6
Texture2D    SpriteTexture        : register(t0);
SamplerState SpriteTextureSampler : register(s0);
#else
Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};
#endif

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color    = input.Color * Tint;
    output.TexCoord = input.TexCoord;
    return output;
}

#if SM6
float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    float4 c = SpriteTexture.Sample(SpriteTextureSampler, input.TexCoord) * input.Color;

    float neighbourDx = abs(QuadReadAcrossX(input.Position.x) - input.Position.x);
    uint  laneSum     = WaveActiveSum(1u);
    uint  laneBits    = WaveActiveCountBits(true);
    float4 roundTrip  = QuadReadAcrossX(QuadReadAcrossX(c));

    bool ok = neighbourDx == 1.0 && laneSum == laneBits && laneSum > 0;
    return ok ? roundTrip : float4(1, 0, 1, 1);
}
#else
float4 MainPS(VertexShaderOutput input) : SV_Target0
{
    return tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
}
#endif

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
