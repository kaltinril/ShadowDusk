// Exercises (G1): top-level non-const MUTABLE globals (a GLSL fragment-scope global). A bare
// `float gAccum;` and an initialized `vec3 gTint = vec3(...)` plus a comma multi-declarator are
// each emitted as an HLSL `static` global (per-invocation mutable semantics), and a helper mutates
// the global state before mainImage reads it back.
float gAccum;
vec3 gTint = vec3(0.2, 0.4, 0.8);
float gA = 0.0, gB = 1.0;

void accumulate(float x)
{
    gAccum += x;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    gAccum = 0.0;
    for (int i = 0; i < 4; i++)
    {
        accumulate(uv.x * float(i));
    }
    float v = (gAccum + gA + gB) * 0.1;
    fragColor = vec4(gTint * v, 1.0);
}
