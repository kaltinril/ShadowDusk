// Exercises (G7b): a local array with the size suffix AFTER the base type (`vec2[4] c = {...};`)
// using a GLSL brace initializer list, plus a plain name-side local array. Emitted as HLSL
// `T name[N] = { ... };` / `T name[N];`.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;

    vec2[4] corners = { vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0), vec2(1.0, 1.0) };

    float acc = 0.0;
    for (int i = 0; i < 4; i++)
    {
        acc += distance(uv, corners[i]);
    }

    fragColor = vec4(acc * 0.25, 0.0, 0.0, 1.0);
}
