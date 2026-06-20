// MUST REJECT: a function-like (parameterized) #define macro is outside the v1 subset
// (only object-like #define constants are supported). The macro is the only out-of-scope construct.
#define SQR(x) ((x) * (x))

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v = SQR(uv.x) + SQR(uv.y);
    fragColor = vec4(v, v, v, 1.0);
}
