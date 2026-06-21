// Exercises B3: a VECTOR equality used in a boolean context. GLSL `a == b` on vectors yields a
// bool there; HLSL `==` on vectors yields a bool-vector, so a scalar `if`/`&&` context needs
// `all(a == b)` (and `!=` needs `any(a != b)`). Covers a bare `if`, a `&&` chain, and a `!=`.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec2 m = iMouse.xy;
    float v = 0.25;
    if (m == vec2(0.0))
    {
        v = 0.5;
    }
    if (uv == vec2(0.5) && m != vec2(1.0))
    {
        v = 0.75;
    }
    fragColor = vec4(v, uv, 1.0);
}
