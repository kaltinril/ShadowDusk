// Single-argument matrix constructors with GLSL semantics:
//  - mat3(1.0) is the identity (scalar on the diagonal, 0 elsewhere)
//  - mat2(s) is a scaled diagonal
//  - mat3(m4) extracts the upper-left 3x3 submatrix of a mat4
// Each expands to an explicit HLSL floatNxN(...) grid.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    mat3 eye = mat3(1.0);
    mat2 sc = mat2(2.0);
    mat4 big = mat4(
        1.0, 2.0, 3.0, 4.0,
        5.0, 6.0, 7.0, 8.0,
        9.0, 10.0, 11.0, 12.0,
        13.0, 14.0, 15.0, 16.0);
    mat3 sub = mat3(big);

    vec3 v = vec3(fragCoord / iResolution.xy, 1.0);
    vec3 r = eye * v + sub * v;
    vec2 w = sc * v.xy;
    fragColor = vec4(r.xy + w, r.z, 1.0);
}
