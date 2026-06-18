// =============================================================================
// ExTernaryHelper.fx  —  ShadowDusk fresh example fixture (issue #106)
// -----------------------------------------------------------------------------
// Provenance : Authored from scratch for the ShadowDusk project (issue #106).
//              Project-owned (same license as the repository). NOT derived from
//              any third-party shader.
// Purpose    : Pin the issue-#106 class — a helper function that RETURNS a
//              ternary built from a relational operator, called from the pixel
//              entry point, plus a relational/ternary used directly in the PS
//              body. VS+PS sprite path (the validated shape).
// Exercises  : ternary `?:` return from a helper, relational ops (<=, >, <),
//              ternary in entry body, helper call from entry, VS mul-transform.
// Regression : The #106 canonical case. Before the FxPreParser fix, a relational
//              operator (`<`, `<=`, `>`, `>=`) in a shader body was misparsed as
//              an FX annotation and the compile failed loudly with FX0001.
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

// The issue-#106 shape: a helper whose body is a single ternary over a relational op.
float Threshold(float value)
{
    return value <= 0.5f ? 0.0f : 1.0f;
}

// A second helper returning a relational-derived float (no ternary) for contrast.
float Band(float value, float lo, float hi)
{
    return (value > lo && value < hi) ? 1.0f : 0.0f;
}

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
    float4 tex = tex2D(SpriteTextureSampler, input.TexCoord) * input.Color;

    float thresholded = Threshold(input.TexCoord.x);
    float band        = Band(input.TexCoord.y, 0.25f, 0.75f);

    // Ternary directly in the entry body as well.
    float3 rgb = thresholded >= 0.5f ? tex.rgb : tex.rgb * 0.25f;
    rgb = lerp(rgb, float3(1, 1, 1), band);

    return float4(rgb, tex.a);
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
