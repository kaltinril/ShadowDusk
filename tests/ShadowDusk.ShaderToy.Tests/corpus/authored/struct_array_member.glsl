// A struct with a fixed-size array member (`float w[4];`). HLSL allows a struct member array directly,
// so the converter emits it; the size may be a literal or a #define-d / const-int constant.
#define NW 4

struct Kernel
{
    float w[NW];
    vec3 tint;
};

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    Kernel k;
    k.w[0] = 0.1; k.w[1] = 0.2; k.w[2] = 0.3; k.w[3] = 0.4;
    k.tint = vec3(0.2, 0.6, 0.9);
    float s = k.w[0] + k.w[1] + k.w[2] + k.w[3];
    fragColor = vec4(k.tint * s, 1.0);
}
