// ExMat3UniformMember.fx — ShadowDusk fresh example fixture (Phase 43C, F6 scope)
// Expect: FAILS SD0210 on the OpenGL target.
//
// A `float3x3` free uniform. The GL model covers float/float2/float3/float4 and
// square float4x4 (plus arrays of those); a mat3's three-register std140 layout
// is not modelled. Before Phase 43C the member was silently dropped and the
// emitted GLSL referenced the deleted `_Globals` block — invalid GLSL with exit
// code 0. The sanctioned staged scope is a LOUD compile error instead.

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

float3x3 ColorTransform;

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 pixel = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    return float4(mul(pixel.rgb, ColorTransform), pixel.a);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile ps_3_0 MainPS();
    }
}
