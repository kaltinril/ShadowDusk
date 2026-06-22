// An unsigned-integer literal (123U) drives uint / uvec bit arithmetic (typically an integer hash),
// which has no faithful mapping to the float-based subset. Out of scope: a loud, located reject AT the
// literal (instead of the stray 'U' surfacing later as a confusing "expected ')'" parse error).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    float h = float(374761393U);
    fragColor = vec4(fract(h * 1e-9), 0.0, 0.0, 1.0);
}
