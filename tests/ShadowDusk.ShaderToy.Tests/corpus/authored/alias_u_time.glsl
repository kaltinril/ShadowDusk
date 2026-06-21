// Exercises (G3): a glslViewer/KodeLife exact-type uniform alias. `uniform float time;` folds onto
// the ShaderToy built-in iTime (same type), so references to `time` Just Work without exposing a
// separate parameter. (A type-MISMATCHED alias, e.g. vec2 u_resolution vs vec3 iResolution, is NOT
// folded — it is exposed verbatim as a custom uniform instead; that case is covered by a unit test.)
uniform float time;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v = 0.5 + 0.5 * sin(time + uv.x * 6.2831853);
    fragColor = vec4(v, v, v, 1.0);
}
