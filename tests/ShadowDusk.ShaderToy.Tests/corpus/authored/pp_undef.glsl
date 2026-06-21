// Exercises: #undef. SCALE is defined, used, then #undef'd and redefined with a different value.
// The use on line "a" sees 2.0; after #undef + redefine the use on line "b" sees 4.0. An #ifdef
// SCALE after the redefine is therefore active again.
#define SCALE 2.0

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float a = uv.x * SCALE;
#undef SCALE
#define SCALE 4.0
    float b = uv.y * SCALE;
    float v = 0.0;
#ifdef SCALE
    v = a + b;
#endif
    fragColor = vec4(v, v, v, 1.0);
}
