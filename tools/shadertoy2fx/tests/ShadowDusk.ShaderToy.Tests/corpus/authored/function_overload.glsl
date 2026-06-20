// Function overloading: two helpers named `sd_scale` with different parameter signatures. GLSL and
// HLSL both allow same-name functions distinguished by signature; the converter emits BOTH overloads
// and HLSL resolves each call by its argument types. (A true redefinition — identical signature —
// would still be an error, but these differ.)
float sd_scale(float x)
{
    return x * 2.0;
}

vec2 sd_scale(vec2 v)
{
    return v * 3.0;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float a = sd_scale(uv.x);
    vec2 b = sd_scale(uv);
    fragColor = vec4(b, a, 1.0);
}
