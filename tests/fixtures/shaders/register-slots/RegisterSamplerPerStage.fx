//-----------------------------------------------------------------------------
// RegisterSamplerPerStage.fx: ONE texture read through one sampler in the
// vertex shader and a DIFFERENT sampler in the pixel shader.
//
// Each compiled shader is its own Vulkan descriptor layout, so the pixel shader
// reads Tex1 as one combined descriptor at slot 0, where SpriteBatch binds its
// sprite (MonoGame's SpriteBatch sets GraphicsDevice.Textures[0] again after it
// applies the pass), and the vertex shader reads the Tex1 parameter at its own
// vertex slot 0. ShadowDusk's Vulkan binding rewrite used to pair Tex1 once for
// the whole file, with the vertex shader's sampler (the first call in the file),
// so the pixel shader read an ALIAS of Tex1 on slot 1: the Tex1 parameter, not
// the sprite. The rewrite now runs per compiled shader.
//
// mgfxc 3.8.5 /Profile:Vulkan builds this file with the pixel shader's sampler as
// a standalone SAMPLER descriptor and no image. The committed golden
// tests/fixtures/golden/Vulkan/RegisterSamplerPerStage.mgfx is mgfxc 3.8.5 on the
// same file with both stages reading through VertexSampler (same slots; no state
// block, so the runtime's sampler state applies either way). Render row:
// validation/VsDrivenVulkan -- regslots (Tex1 = a solid colour, the sprite = the
// cat: the output is cat * colour, where the old layout drew colour * colour).
//-----------------------------------------------------------------------------

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#elif SM6
#define VS_SHADERMODEL vs_6_0
#define PS_SHADERMODEL ps_6_0
#else
#define VS_SHADERMODEL vs_4_0
#define PS_SHADERMODEL ps_4_0
#endif

Texture2D Tex1 : register(t0);
SamplerState VertexSampler : register(s0);
SamplerState PixelSampler : register(s1);
float4x4 WVP;

struct VSOutput
{
    float4 Position : SV_Position;
    float4 Color    : COLOR0;
    float2 UV       : TEXCOORD0;
};

VSOutput MainVS(float4 position : POSITION0, float4 color : COLOR0, float2 uv : TEXCOORD0)
{
    VSOutput o;
    o.Position = mul(position, WVP);
    o.Color = color * Tex1.SampleLevel(VertexSampler, uv, 0);
    o.UV = uv;
    return o;
}

float4 MainPS(VSOutput input) : SV_Target0
{
    return Tex1.Sample(PixelSampler, input.UV) * input.Color;
}

technique T
{
    pass P
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
