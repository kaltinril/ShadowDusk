// `half` GLOBALS, the XNA-era spelling of a float uniform (`half4 Tint = ...;`).
//
// `mgfxc /Profile:DirectX_11` runs fxc with D3DCOMPILE_ENABLE_BACKWARDS_COMPATIBILITY, under
// which a `half` / `halfN` / `halfNxM` global or cbuffer member IS a `float` (without the flag
// fxc refuses it: "X3650: global variables cannot use the 'half' type in ps_4_0_level_9_1. To
// treat this variable as a float, use the backwards compatibility flag."). vkd3d-shader does
// the same only when handed VKD3D_SHADER_COMPILE_OPTION_BACKCOMPAT_ALLOW_HALF_GLOBALS; without
// it the DirectX target failed with "E5002: The half type is not allowed for global variables."
// at the first declaration below. With it the effect reflects, initializes and renders exactly
// like its `float` twin (the RDEF type NAME keeps the author's `half4`, as fxc's own blob does).
//
// `half` in a function signature and a local is covered too (cbuffer members are in the
// integration tests: Vulkan refuses a second constant buffer, SD0026). An identifier
// that merely STARTS with `half` (halfWidth) must stay an identifier: the fix is a compile
// option, never a text substitution.
//
// FNA is not a target for this file: `fxc /T fx_2_0` (the FNA reference, default flags)
// refuses a `half` global with the same X3650, and ShadowDusk's FNA target refuses it with
// vkd3d's E5002 at the same line and column.

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

half4 Tint = half4(1.0, 0.5, 0.25, 1.0);
half2 Offset = half2(0.125, 0.0625);
half  Strength = 0.75;
half3 Bias;
half4x4 ColorMatrix = { 0.5, 0.0, 0.0, 0.0,
                        0.0, 1.0, 0.0, 0.0,
                        0.25, 0.0, 1.0, 0.0,
                        0.0, 0.0, 0.0, 1.0 };
half  Weights[2] = { 0.25, 0.5 };

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

half4 Shade(half4 c, half k)
{
    half4 shaded = c * k;
    return shaded;
}

float4 MainPS(float4 position : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    half halfWidth = 0.5;
    float4 texel = tex2D(SpriteTextureSampler, uv + Offset) * color;
    float4 graded = mul(texel, ColorMatrix);
    float4 lit = Shade(graded * Tint, Strength + (Weights[0] + Weights[1]) * halfWidth);
    return lit + float4(Bias, 0);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
