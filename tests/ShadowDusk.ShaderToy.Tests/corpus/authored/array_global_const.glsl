// Exercises (G7a): a global const array with the size suffix AFTER the base type
// (`const float[N] name = {...};`) using a GLSL brace initializer list. Accepted and emitted as
// HLSL `static const T name[N] = { ... };`.
const float[4] kWeights = float[4](0.4, 0.3, 0.2, 0.1);
const vec3[2] kPalette = { vec3(0.1, 0.2, 0.9), vec3(0.9, 0.4, 0.1) };

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float acc = 0.0;
    for (int i = 0; i < 4; i++)
    {
        acc += kWeights[i] * uv.x;
    }

    vec3 col = mix(kPalette[0], kPalette[1], acc);
    fragColor = vec4(col, 1.0);
}
