// Exercises: polar coordinates via atan(y,x), an angular mod fold (the mod-sign trap), a mat2
// rotation (the column-major matrix-order trap), and a procedural ring/spoke pattern. Animated.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;

    // Spin the whole field with a 2x2 rotation.
    float ca = iTime * 0.3;
    mat2 spin = mat2(cos(ca), -sin(ca),
                     sin(ca),  cos(ca));
    uv = spin * uv;

    float r = length(uv);
    float a = atan(uv.y, uv.x);

    // Kaleidoscope fold: wrap the angle into N equal wedges, then mirror each wedge.
    float segments = 6.0;
    float wedge = 6.2831853 / segments;
    a = mod(a, wedge);
    a = abs(a - 0.5 * wedge); // mirror about the wedge center

    // Procedural pattern in the folded (a, r) space.
    float rings = 0.5 + 0.5 * sin(r * 18.0 - iTime * 2.0);
    float spokes = 0.5 + 0.5 * sin(a * 24.0);
    float pattern = rings * spokes;

    vec3 col = mix(
        vec3(0.10, 0.20, 0.45),
        vec3(0.95, 0.75, 0.25),
        pattern);
    col *= smoothstep(1.1, 0.2, r); // vignette toward the edges

    fragColor = vec4(col, 1.0);
}
