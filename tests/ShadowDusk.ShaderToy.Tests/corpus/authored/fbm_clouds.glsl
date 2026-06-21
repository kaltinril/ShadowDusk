// Exercises: value noise built from a hash (fract/floor/dot), bilinear mix interpolation, and a
// 5-octave fbm for-loop, animated by iTime. Stresses hashing, fract/floor/mix, mat2 scroll, loops.
float hash(vec2 p)
{
    // A classic sin-dot hash - cheap, deterministic, no textures.
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float valueNoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    // Smoothstep-like fade for C1 continuity.
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
    mat2 rot = mat2(1.6, 1.2, -1.2, 1.6); // rotate+scale between octaves to hide grid axes
    for (int i = 0; i < 5; i++)
    {
        sum += amp * valueNoise(p);
        p = rot * p;
        amp *= 0.5;
    }
    return sum;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec2 p = uv * 3.0;
    p.x += iTime * 0.1; // drift the cloud field

    float n = fbm(p);
    float clouds = smoothstep(0.35, 0.75, n);

    vec3 sky = mix(vec3(0.30, 0.55, 0.85), vec3(0.65, 0.80, 0.95), uv.y);
    vec3 col = mix(sky, vec3(1.0), clouds);

    fragColor = vec4(col, 1.0);
}
