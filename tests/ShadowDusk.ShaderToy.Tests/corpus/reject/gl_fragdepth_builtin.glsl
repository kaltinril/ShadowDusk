// gl_FragDepth (per-fragment depth output) has no meaning for a 2D fullscreen image pass. It is a
// known GL stage built-in; the reject names it precisely rather than "undeclared identifier".
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    gl_FragDepth = uv.x;
    fragColor = vec4(uv, 0.0, 1.0);
}
