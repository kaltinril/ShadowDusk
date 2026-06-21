// A host-template placeholder ($speed): the shader is parameterized by a host that substitutes a value
// for the $-token before compiling. The converter cannot resolve a host-substituted value, so this is
// a loud, named reject (not a runaway macro expansion — the C blue-paint rule is honored).
#define speed $speed
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv * speed, 0.0, 1.0);
}
