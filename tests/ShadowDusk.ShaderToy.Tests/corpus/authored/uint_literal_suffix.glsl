// Exercises unsigned-integer literal suffixes ('u'/'U', including hex), the standard uint-hash
// idiom. The suffix lexes as part of the literal and is emitted verbatim (HLSL spells uint
// literals identically); this file used to be a reject while uint mapped to signed int.
uint pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    uint h = pcg(uint(fragCoord.x) + 1920u * uint(fragCoord.y));
    float v = float(h & 0xFFu) / 255.0;
    fragColor = vec4(v, v, v, 1.0);
}
