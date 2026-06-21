// Phase 46: a `switch` statement is now SUPPORTED — lowered to an if/else-if/else chain (portable to
// SM3 / FNA, which have no native switch). Covers multiple case labels, stacked labels sharing one
// body, and the default arm. Every arm terminates with `break` (no fall-through).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    int band = int(uv.x * 4.0);
    vec3 col;
    switch (band)
    {
        case 0:
            col = vec3(1.0, 0.0, 0.0);
            break;
        case 1:
        case 2:
            // Stacked labels share one body.
            col = vec3(0.0, 1.0, 0.0);
            break;
        default:
            col = vec3(0.0, 0.0, 1.0);
            break;
    }
    fragColor = vec4(col, 1.0);
}
