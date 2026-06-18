// =============================================================================
// ExArrayTernaryAssign.fx  —  ShadowDusk fresh example fixture (Phase 45, B7)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (Phase 45) on
//              2026-06-17. Project-owned (same license as the repository). NOT
//              derived from any third-party shader — see docs/test-shader-corpus.md.
// Purpose    : Pin Phase-45 bug B7 (the issue-#106 residual) — an ARRAY-INDEXED
//              relational operator whose ternary arm contains an ASSIGNMENT, used
//              inside the pixel-shader body:
//                  thresholds[i] < y ? acc = w : acc
//              The FxLexer drops '?'/':'/'['/']' , so this tokenizes to
//              'thresholds i < y acc = w acc'. The 'y acc =' tail satisfies the
//              local annotation-shape guard (Type Name = Value), so a purely-local
//              discriminator cannot tell it apart from a real '< Type Name = … >'
//              annotation. The fix gates the global-parameter annotation strip on
//              brace depth 0, so this expression (in a function body, depth >= 1)
//              can never be misread as an annotation.
// Exercises  : array uniform indexed in the PS body, relational '<' with an
//              array-subscript left operand, assignment inside a ternary arm,
//              a second 'name[i] >= name[j]' relational over two subscripts.
// Regression : Before the fix, the array-indexed relational + ternary-assignment
//              was misparsed as an FX annotation and the compile failed (FX0001).
// Targets    : OpenGL + DirectX_11 + FNA (all-runtime SM3 / fx_2_0 subset).
// =============================================================================
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

matrix MatrixTransform;

// Four ascending edges; the PS bands the sprite by comparing against them.
float Thresholds[4]; // e.g. { 0.2, 0.4, 0.6, 0.8 }

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
    float4 Position : SV_POSITION;
    float4 Color    : COLOR0;
    float2 TexCoord : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color    = input.Color;
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float4 col = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    float x   = input.TexCoord.x;
    float acc = 0.0f;
    float w   = 0.25f;

    // B7: array-subscript left operand of '<', with an ASSIGNMENT in a ternary arm.
    // 'Thresholds[0] < x ? acc = w : acc' -> tokens 'Thresholds 0 < x acc = w acc'
    // whose 'x acc =' tail mimics an annotation entry once '?'/':'/'['/']' are gone.
    acc = Thresholds[0] < x ? acc = w        : acc;
    acc = Thresholds[1] < x ? acc = w * 2.0f : acc;
    acc = Thresholds[2] < x ? acc = w * 3.0f : acc;
    acc = Thresholds[3] < x ? acc = w * 4.0f : acc;

    // A relational over TWO array subscripts, for extra coverage.
    float hi = (Thresholds[3] >= Thresholds[0]) ? 1.0f : 0.0f;

    col.rgb *= saturate(acc) * hi;
    return col;
}

technique BandedSprite
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
