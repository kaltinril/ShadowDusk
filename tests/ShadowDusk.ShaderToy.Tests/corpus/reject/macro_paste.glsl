// MUST REJECT: the token-paste operator '##' inside a macro body is outside the supported subset
// (we do not implement '##'/'#'; rejecting loudly is correct rather than mis-expanding). The macro
// is the only out-of-scope construct.
#define CAT(a, b) a ## b

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float CAT(my, Var) = uv.x;
    fragColor = vec4(myVar, myVar, myVar, 1.0);
}
