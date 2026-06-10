// Phase 39 — FNA fx_2_0 multi-pass / render-state coverage fixture.
//
// A D3D9-style SM2 effect with LITERAL vs_2_0/ps_2_0 profiles exercising what no
// other fixture covers under the FNA target in one file:
//   - two techniques (MojoShader keeps them all; FNA exposes CurrentTechnique),
//   - a first technique with TWO passes,
//   - in-pass render states (AlphaBlendEnable/SrcBlend/DestBlend/CullMode — all in
//     FNA's honored set, mapped to D3D9-domain ops 13/6/7/8),
//   - a vertex shader AND pixel shaders (per-pass-stage shader objects),
//   - a float4 uniform read by the PS (CTAB FLOAT4 constant -> effect parameter),
//   - a texture + sampler_state pair (the usage==1 sampler->texture name map).
//
// Consumed by FnaCompileFixtureTests only (fixture suites use explicit lists; no
// test auto-globs this directory).

texture SceneTexture;
sampler TexSampler = sampler_state
{
    Texture = <SceneTexture>;
    MinFilter = LINEAR;
};

float4 Tint;

struct VsInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VsOutput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

VsOutput MainVS(VsInput input)
{
    VsOutput output;
    output.Position = input.Position;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 TintedTexturePS(float2 uv : TEXCOORD0) : COLOR0
{
    return tex2D(TexSampler, uv) * Tint;
}

float4 PlainColorPS() : COLOR0
{
    return float4(0.0, 1.0, 0.0, 1.0);
}

technique MultiPass
{
    pass Blend
    {
        AlphaBlendEnable = TRUE;
        SrcBlend = SRCALPHA;
        DestBlend = INVSRCALPHA;
        CullMode = NONE;
        VertexShader = compile vs_2_0 MainVS();
        PixelShader = compile ps_2_0 TintedTexturePS();
    }
    pass Solid
    {
        PixelShader = compile ps_2_0 PlainColorPS();
    }
}

technique SinglePass
{
    pass Only
    {
        PixelShader = compile ps_2_0 TintedTexturePS();
    }
}
