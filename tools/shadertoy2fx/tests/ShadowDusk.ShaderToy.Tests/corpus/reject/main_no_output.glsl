// G2 boundary: a plain-GLSL `void main()` with NO discoverable fragment output — it neither declares a
// top-level `out vec4 <name>;` nor writes the legacy gl_FragColor anywhere. There is nothing to return
// as COLOR0, so this is a loud, located reject rather than an empty/silently-wrong shader.
void main()
{
    vec2 uv = gl_FragCoord.xy / iResolution.xy;
    float brightness = length(uv);
}
