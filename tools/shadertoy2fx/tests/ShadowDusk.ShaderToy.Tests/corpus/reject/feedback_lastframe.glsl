// getLastFrameColor reads the shader's OWN previous-frame output (a feedback / multipass construct).
// A single image pass cannot supply it. Out of scope: loud, named reject.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec4 prev = getLastFrameColor(uv);
    fragColor = prev * 0.95 + vec4(uv, 0.0, 1.0) * 0.05;
}
