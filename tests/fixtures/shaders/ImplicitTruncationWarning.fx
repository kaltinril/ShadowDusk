// Issue #335 — a shader that COMPILES with a non-fatal compiler diagnostic.
//
// `float3 rgb = color;` assigns a float4 to a float3. Every compiler on every route
// accepts it and warns: vkd3d-shader (the DirectX and FNA backends) says
// `W5300: Implicit truncation of vector type.`, DXC (the OpenGL / Vulkan / DirectX 12
// front end) says `implicit truncation of vector type [-Wconversion]`, and `fxc` says
// `X3206: implicit truncation of vector type`. mgfxc never passes /WX, so the reference
// compiler builds this effect too; the warning rides on CompiledShader.Warnings
// (constraint 5: never swallow a compiler's own message), and the pipeline never fails
// a compile over it.
//
// Why it is in the corpus: a successful compile with a warning is the one shape the
// byte-identity gates could not see. The browser vkd3d host used to return the bytes and
// drop vkd3d's message text on success, so this effect compiled to the desktop's exact
// bytes there while CompiledShader.Warnings came back empty (issue #335). The cross-host
// manifest now records the warnings beside the hashes (warnings-manifest.json), the
// node vkd3d gate requires the shim to hand back vkd3d's verbatim message text, and the
// real-browser gate requires the relocated Warnings to equal the desktop's. The fixture
// keeps every other corpus shader honest too: a corpus with no warning-bearing compile
// cannot notice a host that drops warnings.
//
// The warning sits on line 47, column 12 (the `rgb` declarator vkd3d reports), behind
// the preprocessor's macro prelude and this comment block, so the relocation
// (Vkd3dSourceLocator, issue #202) is exercised, not just the text. Keep the line
// numbers above this point stable, or update warnings-manifest.json.

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#elif FNA
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0_level_9_1
#endif

sampler2D SpriteTextureSampler : register(s0);

struct VSOut
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VSOut input) : COLOR
{
    float4 color = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    float3 rgb = color;
    return float4(rgb, color.a);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
