// MUST REJECT: declares a user struct type, which is outside the v1 subset (no user structs).
// Everything else here is valid; the struct is the only out-of-scope construct.
struct Ray
{
    vec3 origin;
    vec3 dir;
};

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    Ray r;
    r.origin = vec3(uv, 0.0);
    r.dir = vec3(0.0, 0.0, 1.0);
    fragColor = vec4(r.origin.xy, r.dir.z, 1.0);
}
