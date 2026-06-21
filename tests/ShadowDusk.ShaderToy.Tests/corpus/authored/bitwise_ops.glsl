// Exercises bitwise operators (& | ^ << >>) and their compound-assign forms (&= |= ^= <<= >>=),
// distinct from the logical && / ||. All pass straight through to HLSL (valid on int).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    int x = int(fragCoord.x);
    int y = int(fragCoord.y);

    int h = (x << 3) ^ (y >> 1);
    h = h & 255;
    h = h | 1;
    h ^= 7;
    h <<= 1;
    h >>= 2;

    bool both = (x > 0) && (y > 0);
    bool either = (x > 100) || (y > 100);
    float t = (both || either) ? 1.0 : 0.0;

    float v = float(h) / 255.0;
    fragColor = vec4(v, v * t, 0.0, 1.0);
}
