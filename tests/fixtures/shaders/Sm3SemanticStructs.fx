// Issue #295 — SM1-3 semantics on STRUCT FIELDS, compiled for an SM4+ target.
//
// Every stage boundary of this effect carries a Shader Model 3 semantic on a struct
// field, with no `SV_` spelling anywhere:
//   - the vertex shader's OUTPUT struct declares its clip position as `POSITION0`,
//   - the pixel shader's INPUT is that same struct (so `POSITION0` again),
//   - the pixel shader RETURNS a struct whose colour field is `COLOR0`.
//
// `fxc` (which `mgfxc /Profile:DirectX_11` runs with backwards compatibility on) maps
// these to SV_Position / SV_Target when compiling vs_4_0 / ps_4_0, which is how a
// MonoGame effect written against SM3 still builds for DirectX. vkd3d-shader does the
// same ONLY when it is handed VKD3D_SHADER_COMPILE_OPTION_BACKWARD_COMPATIBILITY =
// MAP_SEMANTIC_NAMES. Without the option it:
//   - names the position `POSITION` (system value 0) instead of `SV_Position`
//     (system value 1) in the vertex OSGN and the pixel ISGN, so the rasterizer is
//     given no position at all, and
//   - rejects the pixel shader outright: "E5013: Invalid semantic 'COLOR'".
//
// FxPreParser cannot cover this shape: it rewrites a `) : COLOR<n>` RETURN semantic,
// but never a struct FIELD (the struct may be a vertex output, where COLOR is legal).
// So this fixture's DXBC depends on the compile option alone, which makes it the
// corpus case that turns a host passing a different option set (the browser vkd3d
// module did, before issue #295) into a byte mismatch in the vkd3d corpus gate
// (tests/ShadowDusk.BrowserTests/node-test-vkd3d-wasm.mjs). No other corpus fixture
// changes a byte with the option on or off.

#if OPENGL
#define VS_SHADERMODEL vs_3_0
#define PS_SHADERMODEL ps_3_0
#else
#define VS_SHADERMODEL vs_4_0_level_9_1
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

float4x4 WorldViewProjection;
float4   Tint;

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : POSITION0;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

struct PixelShaderOutput
{
    float4 Color : COLOR0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output = (VertexShaderOutput)0;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Color    = input.Color * Tint;
    output.TexCoord = input.TexCoord;
    return output;
}

PixelShaderOutput MainPS(VertexShaderOutput input)
{
    PixelShaderOutput output;
    output.Color = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;
    return output;
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
