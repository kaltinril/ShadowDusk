// MUST REJECT: roundEven (round-half-to-even / banker's rounding) has no faithful HLSL equivalent
// (HLSL round and floor(x+0.5) are both round-half-up), so it is a loud reject rather than a subtly
// different rounding. (The mapping table stays authoritative: unmappable intrinsic = located reject.)
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float q = roundEven(uv.x * 10.0) / 10.0;
    fragColor = vec4(q, uv.y, 0.0, 1.0);
}
