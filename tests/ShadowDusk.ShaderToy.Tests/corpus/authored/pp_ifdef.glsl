// Exercises: #ifdef / #ifndef gating. AA is defined, so the #ifdef AA block is kept and the
// #ifndef AA block is dropped (its body must NOT appear in the emitted .fx). FAST is undefined,
// so #ifdef FAST is dropped and its #else kept. Inactive branches become blank lines.
#define AA

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v;
#ifdef AA
    v = 0.5 + 0.5 * sin(uv.x * 10.0 + iTime);
#endif
#ifndef AA
    v = 1.0;
#endif
#ifdef FAST
    v = 0.0;
#else
    v = v * 0.9;
#endif
    fragColor = vec4(v, v, v, 1.0);
}
