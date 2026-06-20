// Exercises: defined(NAME) and bare `defined NAME` inside an #if expression. HAS_GLOW is defined,
// MISSING is not, so `defined(HAS_GLOW) && !defined MISSING` is true and its branch is kept.
#define HAS_GLOW

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v = length(uv - 0.5);
#if defined(HAS_GLOW) && !defined MISSING
    v = 1.0 - v;
#endif
    fragColor = vec4(v, v, v, 1.0);
}
