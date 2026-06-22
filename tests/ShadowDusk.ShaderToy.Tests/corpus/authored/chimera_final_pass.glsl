// Real-world ShaderToy final pass (CC BY-NC-SA): the image tab of "Chimera's Breath" by nimitz,
// https://www.shadertoy.com/view/4tGfDW (the fluid simulation itself lives in multipass Buffer A-D, which
// is out of scope; this single image pass just displays iChannel0).
//
// Regression for textureLod(s, uv, 0.) -> tex2D: a base-level (lod 0) textureLod used to emit the legacy
// tex2Dlod intrinsic, which does NOT rewrite to a modern Texture method on the OpenGL/DirectX targets
// (FX0012, it compiles only on FNA's fx_2_0 path). Since the single-pass harness binds iChannelN without
// mipmaps, lod 0 is the only level, so a base-level textureLod now lowers to a plain tex2D and compiles on
// every backend. Stored faithfully (shader body unedited); only this header comment was added.

void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec4 col = textureLod(iChannel0, fragCoord/iResolution.xy, 0.);
    if (fragCoord.y < 1. || fragCoord.y >= (iResolution.y-1.))
        col = vec4(0);
    fragColor = col;
}
