// MUST REJECT: calls an intrinsic with no entry in the mapping table (texelFetch); anything
// outside the explicit intrinsic map is a loud reject. The texelFetch call is the only out-of-scope construct.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    ivec2 ip = ivec2(fragCoord);
    vec4 tex = texelFetch(iChannel0, ip, 0);
    fragColor = vec4(tex.rgb, 1.0);
}
