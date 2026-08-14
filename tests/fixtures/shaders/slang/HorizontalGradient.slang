// Uniform-free procedural: left-to-right red->blue lerp. Part of the render-equivalence
// subset: no uniforms and no textures, so the slangc-vs-ShadowDusk pixel comparison needs
// no parameter plumbing on either side.
[shader("fragment")]
float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    return lerp(float4(1, 0, 0, 1), float4(0, 0, 1, 1), uv.x);
}
