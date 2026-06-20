// Exercises (G7 intrinsics): matrixCompMult(a, b) is the COMPONENTWISE matrix product and must emit a
// plain `(a * b)` (HLSL `*` is componentwise on matrices) - NOT the mul()-reordered linear product.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    mat2 a = mat2(uv.x, uv.y, 1.0, 0.0);
    mat2 b = mat2(2.0, 0.5, 0.5, 2.0);
    mat2 c = matrixCompMult(a, b);
    vec2 r = c * uv;
    fragColor = vec4(r, 0.0, 1.0);
}
