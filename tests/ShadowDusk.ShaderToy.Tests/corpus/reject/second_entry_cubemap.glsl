// MUST REJECT: contains a second entry point (mainCubemap), i.e. a non-image / multi-tab shader.
// v1 supports a single-pass image shader (one mainImage) only; the extra entry is out of scope.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.5, 1.0);
}

void mainCubemap(out vec4 fragColor, in vec2 fragCoord, in vec3 rayOri, in vec3 rayDir)
{
    fragColor = vec4(rayDir * 0.5 + 0.5, 1.0);
}
