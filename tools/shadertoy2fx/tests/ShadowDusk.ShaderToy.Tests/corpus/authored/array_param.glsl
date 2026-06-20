// Exercises (G7c): an array function PARAMETER with the size suffix AFTER the type
// (`void f(inout float[4] k)`). HLSL spells the size on the declarator name: `inout float k[4]`.
void scale(inout float[4] k, float s)
{
    for (int i = 0; i < 4; i++)
    {
        k[i] = k[i] * s;
    }
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;

    float vals[4];
    vals[0] = uv.x;
    vals[1] = uv.y;
    vals[2] = uv.x * uv.y;
    vals[3] = 1.0 - uv.x;

    scale(vals, 0.5);

    float acc = vals[0] + vals[1] + vals[2] + vals[3];
    fragColor = vec4(acc * 0.25, 0.0, 0.0, 1.0);
}
