// =============================================================================
// Issue106Repro.fx  —  the verbatim reproducer from GitHub issue #106
// -----------------------------------------------------------------------------
// Provenance : The exact shader posted by the reporter (vchelaru, XnaFiddle) on
//              ShadowDusk issue #106 ("Shader should be able to return ternary
//              values"). Kept verbatim (only this header added) so the canonical
//              reproducer is pinned forever, not just a synthetic stand-in.
// Purpose    : Regression-lock the issue-#106 class with the REAL reported shape:
//              a helper function (`TestEarlyReturn`) whose body uses an equality
//              (`==`), a relational (`<=`), nested `if`, and an EARLY `return`,
//              called from the pixel entry point. Before the FxPreParser fix the
//              `<=` (lexed as `LAngle Equals`) inside the function body matched the
//              global-annotation heuristic and the compile failed with FX0001.
// Exercises  : relational (<=) + equality (==) in a body, nested if, early return,
//              VS+PS sprite path, mul-transform. Companion to the synthetic
//              examples/ExTernaryHelper.fx (which adds explicit `?:` ternaries).
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

struct VertexShaderInput
{
    float4 Position : POSITION0;
    float4 Color : COLOR0;
    float2 TextureCoordinate : TEXCOORD0;
};

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinate : TEXCOORD0;
};

VertexShaderOutput MainVS(VertexShaderInput input)
{
    VertexShaderOutput output;
    output.Position = mul(input.Position, MatrixTransform);
    output.Color = input.Color;
    output.TextureCoordinate = input.TextureCoordinate;
    return output;
}

float TestEarlyReturn(float edge0, float edge1, float value)
{
    if (edge0 == edge1)
    {
        if (value <= edge0)
		{
			return 0.0f;
		}
    }

    return value;
}

float4 MainPS(VertexShaderOutput input) : COLOR0
{
    float result = TestEarlyReturn(0.5f, 0.5f, input.TextureCoordinate.x);
    return float4(result, result, result, 1.0f);
}

technique SpriteDrawing
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
