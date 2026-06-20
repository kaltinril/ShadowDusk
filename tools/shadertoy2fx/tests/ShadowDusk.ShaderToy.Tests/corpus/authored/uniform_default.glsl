// Exercises (G4): a custom uniform with a DEFAULT initializer (valid GLSL 1.20+). The default is
// emitted as the HLSL parameter's initializer so the consumer gets it unless they override; the
// uniform is still a host-drivable effect parameter (reported in UsedUniforms).
uniform float uGain = 1.5;
uniform vec3 uColor = vec3(0.9, 0.3, 0.1);

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uColor * uGain * uv.x, 1.0);
}
