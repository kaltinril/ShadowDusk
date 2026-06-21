// Exercises: object-like #define constants (must be honored/substituted, not rejected).
#define PI 3.14159265
#define TAU (PI * 2.0)
#define SCALE 8.0

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v = 0.5 + 0.5 * sin(uv.x * SCALE * TAU + iTime);
    fragColor = vec4(v, v * 0.5, 1.0 - v, 1.0);
}
