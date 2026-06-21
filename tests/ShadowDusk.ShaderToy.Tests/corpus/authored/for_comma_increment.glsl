// Exercises (parser hardening): the GLSL comma (sequence) operator in a for-loop increment
// (`i++, j--`) and a comma-initialized for header, which the parser previously mishandled.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float acc = 0.0;
    for (int i = 0, j = 4; i < 4; i++, j--)
    {
        acc += float(j) * uv.x * 0.1;
    }

    fragColor = vec4(acc, uv.y, 0.0, 1.0);
}
