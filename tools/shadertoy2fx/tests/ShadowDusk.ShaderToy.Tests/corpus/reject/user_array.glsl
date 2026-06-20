// MUST REJECT: declares a user array (float[]), which is outside the v1 subset (no arrays).
// Everything else here is valid; the array declaration is the only out-of-scope construct.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float weights[3];
    weights[0] = 0.2;
    weights[1] = 0.5;
    weights[2] = 0.3;
    float v = weights[0] * uv.x + weights[1] * uv.y + weights[2];
    fragColor = vec4(v, v, v, 1.0);
}
