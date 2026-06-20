// Array sizes from a #define-d constant, a `const int`, and a const-int EXPRESSION. The preprocessor
// expands the #define to a literal; the parser resolves the const-int names and evaluates the
// `COUNT * 2` expression to a literal size. All become fixed-size HLSL arrays.
#define KSIZE 4
const int COUNT = 3;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    float weights[KSIZE];
    weights[0] = 0.1; weights[1] = 0.2; weights[2] = 0.3; weights[3] = 0.4;

    vec2 pts[COUNT];
    pts[0] = vec2(0.0); pts[1] = vec2(0.5); pts[2] = vec2(1.0);

    float pairs[COUNT * 2];
    pairs[0] = 1.0;

    vec2 uv = fragCoord / iResolution.xy;
    float acc = weights[0] + distance(uv, pts[1]) + pairs[0];
    fragColor = vec4(acc, acc, acc, 1.0);
}
