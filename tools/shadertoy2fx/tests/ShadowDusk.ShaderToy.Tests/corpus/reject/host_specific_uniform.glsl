// A host-specific global (e.g. a terminal-cursor uniform) the converter cannot invent a value for.
// It is not a ShaderToy built-in and is never declared, so it stays a loud "undeclared identifier"
// reject that names it as a host-provided value. We do NOT auto-expose arbitrary unknowns (guessing).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, iCurrentCursor.x, 1.0);
}
