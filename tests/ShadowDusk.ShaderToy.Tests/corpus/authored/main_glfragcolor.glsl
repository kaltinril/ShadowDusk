// G2 plain-GLSL entry mode: legacy `void main()` writing gl_FragColor, reading gl_FragCoord.
// A UV gradient (the same shape as gradient_uv.glsl, but in main() form) so it exercises the
// gl_FragCoord -> harness-pixel-coord mapping and the gl_FragColor -> PS-return bridge.
void main()
{
    vec2 uv = gl_FragCoord.xy / iResolution.xy;
    gl_FragColor = vec4(uv.x, uv.y, 0.5, 1.0);
}
