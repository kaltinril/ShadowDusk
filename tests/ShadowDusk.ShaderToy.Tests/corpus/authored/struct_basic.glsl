// Exercises (G6): a user struct with scalar/vector AND a matrix member, the GLSL struct constructor
// Name(...) (must become the generated factory make_Name(...)), member access s.field, and a
// struct-member matrix multiply s.rot * v that MUST still hit the matrix-order trap (mul(v, ...)).
struct Particle
{
    vec2 pos;
    mat2 rot;
    vec3 color;
};

Particle spin(vec2 p, float a)
{
    float c = cos(a), s = sin(a);
    // Name(...) struct constructor with a mat2 member built inline.
    return Particle(p, mat2(c, -s, s, c), vec3(0.2, 0.6, 1.0));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    Particle pt = spin(uv, iTime);
    // s.rot is a mat2 member: this multiply must become mul(pt.pos, pt.rot) (matrix-order trap).
    vec2 q = pt.rot * pt.pos;
    float d = length(q);
    fragColor = vec4(pt.color * (1.0 - d), 1.0);
}
