// Exercises (G3c): a ShaderToy `mainImage` body that references the built-in gl_FragCoord directly
// (a common third-party shape). It aliases the harness pixel coordinate as a float4 (.xy = fragCoord,
// .z = 0, .w = 1), so it resolves without an "undeclared identifier" reject.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = gl_FragCoord.xy / iResolution.xy;
    float d = gl_FragCoord.z + gl_FragCoord.w;
    fragColor = vec4(uv, d * 0.5, 1.0);
}
