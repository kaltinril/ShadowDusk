// texture(iChannel0, vec3) samples iChannel0 as a CUBEMAP (a 3D direction lookup), e.g.
// texture(iChannel0, reflect(rd, n)). The single-pass 2D harness binds each iChannelN as a 2D sampler,
// so this is out of scope: a loud, named reject AT the call (instead of the vec3 coordinate silently
// truncating to 2D and surfacing as an opaque HLSL "-Wconversion" truncation error on generated code).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec3 dir = normalize(vec3(fragCoord / iResolution.xy - 0.5, 1.0));
    fragColor = texture(iChannel0, dir);
}
