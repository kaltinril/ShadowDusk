// Exercises the uint -> int and uvec2/3/4 -> int2/3/4 type mapping (treated as signed int; the
// supported subset has no unsigned type). A common hash idiom uses uint + bitwise ops.
uint hash(uint x)
{
    x ^= x >> 16;
    x = x * 747796405;
    x ^= x >> 13;
    return x;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    uvec2 p = uvec2(int(fragCoord.x), int(fragCoord.y));
    uint h = hash(p.x + p.y * 9999);
    float v = float(h & 255) / 255.0;
    fragColor = vec4(v, v, v, 1.0);
}
