// G2 render-proof: a plain-GLSL `void main()` gradient, the analytic twin of gradient_uv.glsl but in
// main() form. It proves gl_FragCoord's Y maps the SAME way in main() mode as fragCoord does in
// ShaderToy mode: with the bottom-left origin, displayed bottom-left = (R,G)=(0,0) and displayed
// top-right = (1,1). If main()-mode flipped Y wrong, this would render upside-down and the asserts fail.
void main()
{
    vec2 uv = gl_FragCoord.xy / iResolution.xy;
    gl_FragColor = vec4(uv.x, uv.y, 0.5, 1.0);
}
