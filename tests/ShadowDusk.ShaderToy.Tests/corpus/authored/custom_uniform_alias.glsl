// Exercises: the glslViewer alias `u_time` (exact-type float) is folded onto the ShaderToy built-in
// iTime, so the body's u_time references Just Work while a genuinely-custom uniform (uSpeed) is
// exposed verbatim as an effect parameter.
uniform float u_time;
uniform float uSpeed;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float t = u_time * uSpeed;
    vec3 col = 0.5 + 0.5 * cos(t + uv.xyx + vec3(0.0, 2.0, 4.0));
    fragColor = vec4(col, 1.0);
}
