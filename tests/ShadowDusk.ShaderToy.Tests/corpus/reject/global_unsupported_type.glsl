// Must be REJECTED (G1 boundary): a top-level mutable global of an UNSUPPORTED type. `double` is not
// in the v1 subset, so even though mutable globals are now accepted, an unsupported-type global stays
// a loud, located reject (never silently mistranslated).
double gBad;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
}
