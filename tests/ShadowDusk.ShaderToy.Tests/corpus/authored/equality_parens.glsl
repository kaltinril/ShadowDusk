// Exercises B2: a SCALAR equality used directly as an `if` condition must NOT be double-wrapped in
// parentheses. The naive emitter produced `if ((a == 0.0))`, which fxc rejects under
// -Werror,-Wparentheses-equality. The fixed output is `if (a == 0.0)`.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    float a = fragCoord.x / iResolution.x;
    float v = 1.0;
    if (a == 0.0)
    {
        v = 0.0;
    }
    fragColor = vec4(v, a, 0.0, 1.0);
}
