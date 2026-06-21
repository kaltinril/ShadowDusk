// Exercises: nested #if / #elif / #else with correct nesting. The outer #if MODE == 2 is true;
// inside it, the #elif chain selects the COLOR == 1 branch. The unrelated outer #else is dropped.
#define MODE 2
#define COLOR 1

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec3 col;
#if MODE == 1
    col = vec3(uv, 0.0);
#elif MODE == 2
    #if COLOR == 0
        col = vec3(1.0, 0.0, 0.0);
    #elif COLOR == 1
        col = vec3(uv.x, uv.y, 0.5);
    #else
        col = vec3(0.0);
    #endif
#else
    col = vec3(0.0, 0.0, 0.0);
#endif
    fragColor = vec4(col, 1.0);
}
