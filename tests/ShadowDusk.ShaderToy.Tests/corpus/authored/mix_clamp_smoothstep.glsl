// Exercises: mix(->lerp), clamp, smoothstep, fract(->frac) intrinsic renames and semantics.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float t  = clamp(uv.x, 0.0, 1.0);
    float s  = smoothstep(0.2, 0.8, uv.y);
    float f  = fract(uv.x * 5.0);
    vec3 a = vec3(0.1, 0.2, 0.8);
    vec3 b = vec3(0.9, 0.6, 0.1);
    vec3 col = mix(a, b, t) * s + f * 0.1;
    fragColor = vec4(col, 1.0);
}
