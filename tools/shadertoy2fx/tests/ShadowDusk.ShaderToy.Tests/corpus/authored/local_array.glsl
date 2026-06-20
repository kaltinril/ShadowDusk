// Exercises (G7): a local fixed-size array declared then written and read by index, plus a local
// const array initialized with an array constructor. Array indexing infers the element type so a
// later use type-checks.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;

    float samples[4];
    samples[0] = uv.x;
    samples[1] = uv.y;
    samples[2] = uv.x * uv.y;
    samples[3] = 1.0 - uv.x;

    const float kGain[4] = float[](0.4, 0.3, 0.2, 0.1);

    float v = 0.0;
    for (int i = 0; i < 4; i++)
    {
        v += samples[i] * kGain[i];
    }

    fragColor = vec4(v, v, v, 1.0);
}
