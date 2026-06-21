// Exercises (G7): a const global array initialized with a GLSL array constructor float[](...),
// both the unsized type[](...) and the sized type[N](...) forms, and array indexing in a loop.
const float kWeights[3] = float[](0.25, 0.5, 0.25);
const vec3 kPalette[2] = vec3[2](vec3(0.1, 0.2, 0.9), vec3(0.9, 0.4, 0.1));

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float acc = 0.0;
    for (int i = 0; i < 3; i++)
    {
        acc += kWeights[i] * uv.x;
    }

    vec3 col = mix(kPalette[0], kPalette[1], acc);
    fragColor = vec4(col, 1.0);
}
