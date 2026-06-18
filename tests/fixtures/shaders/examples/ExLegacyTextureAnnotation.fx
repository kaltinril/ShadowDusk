// =============================================================================
// ExLegacyTextureAnnotation.fx  —  ShadowDusk fresh example fixture (Phase 45, B4)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B4 — a legacy effect-framework 'texture' object
//              declaration carrying an FX ANNOTATION block:
//                  texture Diffuse < string ResourceName = "wall.png"; >;
//              The annotation block has its own inner ';' separators between '<'
//              and '>'. Before the fix, ConsumeLegacyTextureDecl stopped at the
//              FIRST ';' (the one inside the annotation), so the rewrite to
//              'Texture2D Diffuse;' leaked the trailing '>;' and DXC reported
//              'expected unqualified-id'. The fix tracks angle-bracket depth so
//              only a ';' at depth 0 terminates the declaration; the whole
//              'texture … < … >;' span becomes a clean 'Texture2D Diffuse;'.
//              This is the ubiquitous FX Composer / RenderMonkey / NVIDIA-sample
//              authoring shape.
// Exercises  : legacy 'texture T < … >;' with a single-entry string annotation,
//              a second 'texture T < … >;' with a MULTI-entry annotation
//              (string + int), each bound through a sampler_state + tex2D.
// Regression : Before the fix, the texture-level annotation leaked '>;' into the
//              rewritten HLSL and the compile failed inside DXC.
// Targets    : OpenGL + DirectX_11 + FNA. On FNA (PreserveSm3) the legacy
//              'texture' type passes through to vkd3d and the annotation is removed
//              by the generic annotation strip — it was never broken there, and
//              the fixture pins that it stays working.
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// Legacy texture object types with trailing FX annotations (the B4 shape).
texture Diffuse < string ResourceName = "wall.png"; >;
texture Detail  < string ResourceName = "detail.png"; int UIOrder = 2; >;

sampler DiffuseSampler = sampler_state { Texture = <Diffuse>; };
sampler DetailSampler  = sampler_state { Texture = <Detail>;  };

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float4 baseColor   = tex2D(DiffuseSampler, input.TexCoord);
    float4 detailColor = tex2D(DetailSampler, input.TexCoord);
    return baseColor * detailColor * input.Color;
}

technique LegacyTextureAnnotationExample
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
