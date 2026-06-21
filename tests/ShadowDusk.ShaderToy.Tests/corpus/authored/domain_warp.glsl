// Exercises: domain warping - fbm whose input coordinate is itself displaced by two more fbm
// evaluations (nested helper calls + loops + accumulating vec2 offsets), animated by iTime.
float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(269.5, 183.3))) * 43758.5453123);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash(i + vec2(0.0, 0.0));
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p)
{
    float sum = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 4; i++)
    {
        sum += amp * valueNoise(p);
        p *= 2.0;
        amp *= 0.5;
    }
    return sum;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec2 p = uv * 4.0;
    float t = iTime * 0.2;

    // First warp layer q, then second warp layer r, then sample fbm at the doubly-warped point.
    vec2 q = vec2(
        fbm(p + vec2(0.0, 0.0)),
        fbm(p + vec2(5.2, 1.3)));

    vec2 r = vec2(
        fbm(p + 4.0 * q + vec2(1.7 + t, 9.2)),
        fbm(p + 4.0 * q + vec2(8.3, 2.8 - t)));

    float f = fbm(p + 4.0 * r);

    vec3 col = mix(vec3(0.10, 0.10, 0.35), vec3(0.95, 0.55, 0.20), f);
    col = mix(col, vec3(0.20, 0.70, 0.50), clamp(length(q), 0.0, 1.0));
    col = mix(col, vec3(0.95, 0.95, 0.95), clamp(r.x * r.x, 0.0, 1.0));

    fragColor = vec4(col, 1.0);
}
