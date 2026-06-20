// Exercises: function-like #define macros (argument substitution at the call site).
// SQR squares its argument; MIXC blends two colors. Both must expand inline, not be rejected.
#define SQR(x) ((x) * (x))
#define MIXC(a, b, t) mix(a, b, t)

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float d = SQR(uv.x - 0.5) + SQR(uv.y - 0.5);
    vec3 col = MIXC(vec3(0.1, 0.2, 0.3), vec3(0.9, 0.8, 0.2), d);
    fragColor = vec4(col, 1.0);
}
