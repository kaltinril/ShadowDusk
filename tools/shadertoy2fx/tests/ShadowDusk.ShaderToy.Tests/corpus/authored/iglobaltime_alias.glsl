// Exercises L1 (auto-handle exception b): the deprecated ShaderToy alias `iGlobalTime` must map to
// the canonical `iTime` so the reference resolves instead of leaking to a compile error. After
// conversion the emitted .fx must reference iTime, never the deprecated spelling. (The analogous
// iGlobalFrame -> iFrame alias is covered by a unit assertion; iFrame is an int uniform the GL
// target does not model, so it is kept out of this compile-swept fixture.)
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    float t = iGlobalTime;
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.5 + 0.5 * sin(t), 1.0);
}
