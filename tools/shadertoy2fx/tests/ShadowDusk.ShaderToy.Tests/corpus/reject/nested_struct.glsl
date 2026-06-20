// MUST REJECT: a struct with a nested/inline struct member, which is outside the v1 subset.
// A flat struct (scalar/vector/matrix members) is now supported (G6); an inline nested struct is not.
struct Material
{
    vec3 albedo;
    struct { float r; float g; } inner;
};

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    Material m;
    m.albedo = vec3(uv, 0.5);
    fragColor = vec4(m.albedo, 1.0);
}
