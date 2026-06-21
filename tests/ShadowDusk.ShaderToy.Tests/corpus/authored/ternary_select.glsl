// Exercises: the ternary ?: operator inside an expression (relational + ternary, the issue #106 shape).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float band = uv.y <= 0.5 ? 0.0 : 1.0;
    float edge = uv.x > 0.5 ? uv.x : 1.0 - uv.x;
    fragColor = vec4(band, edge, band * edge, 1.0);
}
