// G2 boundary: the shader defines BOTH a ShaderToy `mainImage` and a plain-GLSL `main`. The converter
// cannot know which the author intends, so this is an ambiguous, loud, located reject (never a guess).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
}

void main()
{
    gl_FragColor = vec4(gl_FragCoord.xy / iResolution.xy, 0.0, 1.0);
}
