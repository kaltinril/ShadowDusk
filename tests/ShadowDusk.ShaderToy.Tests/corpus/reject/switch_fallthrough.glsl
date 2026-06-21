// MUST REJECT: a `switch` with true C FALL-THROUGH — a non-empty `case` body with no terminating
// `break`/`return` falls into the next case. Lowering fall-through to an if/else chain would change
// control flow, so it stays a loud, located reject (add a `break;` to each case). A plain `switch`
// with break-terminated arms is now supported (see authored/switch_statement.glsl).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    int band = int(uv.x * 3.0);
    vec3 col = vec3(0.0);
    switch (band)
    {
        case 0:
            col.r = 1.0;
            // NO break here: falls through into case 1 (real C fall-through).
        case 1:
            col.g = 1.0;
            break;
        default:
            col.b = 1.0;
            break;
    }
    fragColor = vec4(col, 1.0);
}
