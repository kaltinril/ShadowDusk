// ExIntUniformMember.fx — ShadowDusk fresh example fixture (Phase 43C, F6 scope)
// Expect: FAILS SD0210 on the OpenGL target.
//
// An `int` free uniform. MojoShader models integer uniforms in a SEPARATE
// register set ({vs,ps}_uniforms_ivec4) that ShadowDusk's GL model does not
// emit yet. Before Phase 43C this member was SILENTLY DROPPED: the emitted GLSL
// still referenced `_Globals.Mode` on a deleted block — invalid GLSL with exit
// code 0, failing only inside the consumer's game at Effect-load time. The
// sanctioned staged scope is a LOUD compile error instead.

Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state
{
    Texture = <SpriteTexture>;
};

int    Mode;
float4 TintColor;

struct VertexShaderOutput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float2 TextureCoordinates : TEXCOORD0;
};

float4 MainPS(VertexShaderOutput input) : COLOR
{
    float4 pixel = tex2D(SpriteTextureSampler, input.TextureCoordinates) * input.Color;
    return Mode > 0 ? pixel * TintColor : pixel;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile ps_3_0 MainPS();
    }
}
