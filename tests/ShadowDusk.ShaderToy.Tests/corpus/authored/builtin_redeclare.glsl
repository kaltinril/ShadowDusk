// Exercises L1 (auto-handle exception a): a redundant re-declaration of KNOWN ShaderToy built-in
// uniforms must be DROPPED (the harness already injects them), letting the shader convert instead of
// rejecting. All three declared uniforms here are built-ins, so the body resolves cleanly.
uniform float iTime;
uniform vec3 iResolution;
uniform vec4 iMouse;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec2 m = iMouse.xy / iResolution.xy;
    float d = length(uv - m);
    fragColor = vec4(d, sin(iTime), 0.5, 1.0);
}
