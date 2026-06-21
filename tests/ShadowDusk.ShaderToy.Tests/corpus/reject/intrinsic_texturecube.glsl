// textureCube samples a CUBEMAP, which has no faithful 2D sampler2D / SM3 mapping. Out of scope:
// loud, named reject.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec3 dir = vec3(fragCoord / iResolution.xy, 1.0);
    fragColor = textureCube(iChannel0, dir);
}
