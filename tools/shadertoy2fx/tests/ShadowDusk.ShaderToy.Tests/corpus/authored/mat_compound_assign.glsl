// Exercises B1: a matrix COMPOUND assignment `v *= M` (M = mat2). GLSL `p *= rot(a)` means
// p = M*p, which under the A*B -> mul(B,A) rule must emit `p = mul(p, rot(a))`, never the invalid
// `float2 *= float2x2`. A scalar `*=` in the same body must stay component-wise.
mat2 rot(float a)
{
    float c = cos(a);
    float s = sin(a);
    return mat2(c, -s, s, c);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    p *= rot(iTime);          // matrix compound assignment (B1)
    p *= 2.0;                 // scalar compound assignment stays component-wise
    float stripes = 0.5 + 0.5 * sin(p.x * 10.0);
    fragColor = vec4(stripes, stripes, stripes, 1.0);
}
