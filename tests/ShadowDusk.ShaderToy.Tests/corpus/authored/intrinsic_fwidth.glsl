// Exercises (G7 intrinsics): fwidth (anti-aliased edge width) maps to the same-named HLSL intrinsic.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float d = length(uv - 0.5);
    float w = fwidth(d);
    float ring = smoothstep(0.3 + w, 0.3 - w, d);
    fragColor = vec4(ring, ring, ring, 1.0);
}
