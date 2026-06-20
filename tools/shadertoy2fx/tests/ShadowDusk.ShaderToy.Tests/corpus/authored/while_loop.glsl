// Exercises: a bounded while-loop (distinct from for-loop) accumulating a value.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float acc = 0.0;
    int i = 0;
    while (i < 5)
    {
        acc += abs(sin(uv.x * float(i + 1) * 3.0));
        i++;
    }
    float v = acc / 5.0;
    fragColor = vec4(v, v, v, 1.0);
}
