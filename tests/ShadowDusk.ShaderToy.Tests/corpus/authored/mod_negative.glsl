// Exercises: the MOD TRAP - mod() on a centered (possibly-negative) coordinate; GLSL mod != HLSL fmod for negatives.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    vec2 cell = mod(p * 8.0, 1.0);
    float checker = step(0.5, cell.x) == step(0.5, cell.y) ? 1.0 : 0.0;
    fragColor = vec4(checker, checker, checker, 1.0);
}
