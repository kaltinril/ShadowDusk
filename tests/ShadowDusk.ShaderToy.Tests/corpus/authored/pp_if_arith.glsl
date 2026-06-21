// Exercises: #if with an integer constant expression (arithmetic + comparison + macro expansion).
// QUALITY is 3; (QUALITY * 2 + 1 == 7) is true, so its branch is kept and the #else dropped.
#define QUALITY 3

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v;
#if QUALITY * 2 + 1 == 7
    v = length(uv - 0.5);
#else
    v = 0.0;
#endif
#if (1 << 3) > 4 && !0
    v = v * 0.5;
#endif
    fragColor = vec4(v, v, v, 1.0);
}
