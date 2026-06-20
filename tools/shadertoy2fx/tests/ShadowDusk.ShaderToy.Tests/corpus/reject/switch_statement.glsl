// MUST REJECT: uses a switch statement, which is outside the v1 subset (no switch).
// Everything else here is valid; the switch is the only out-of-scope construct.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    int band = int(uv.x * 3.0);
    vec3 col;
    switch (band)
    {
        case 0: col = vec3(1.0, 0.0, 0.0); break;
        case 1: col = vec3(0.0, 1.0, 0.0); break;
        default: col = vec3(0.0, 0.0, 1.0); break;
    }
    fragColor = vec4(col, 1.0);
}
