// Exercises: iMouse usage - distance from the mouse cursor controls brightness.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv    = fragCoord / iResolution.xy;
    vec2 mouse = iMouse.xy / iResolution.xy;
    float d = length(uv - mouse);
    float glow = 1.0 - smoothstep(0.0, 0.3, d);
    fragColor = vec4(glow, glow * 0.6, 0.2, 1.0);
}
