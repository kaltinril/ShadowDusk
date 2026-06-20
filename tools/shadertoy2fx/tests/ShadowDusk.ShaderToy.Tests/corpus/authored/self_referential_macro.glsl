// A self-referential object macro (`#define WIDTH (WIDTH_BASE)` then a macro that references the name
// it is defining). Per the C "blue-paint" rule, a macro's own name in its expansion is left as a plain
// identifier rather than re-expanded, so this converts instead of triggering a runaway-expansion reject.
#define SCALE 2.0
#define SCALE_PLUS (SCALE + 1.0)

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv * SCALE_PLUS, SCALE, 1.0);
}
