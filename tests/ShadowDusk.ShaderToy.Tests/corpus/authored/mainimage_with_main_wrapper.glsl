// G2 both-entries case: a ShaderToy `mainImage` PLUS a standalone `void main()` wrapper, the common
// glslViewer / Bonzomatic / desktop-runner shape that calls mainImage so the same .glsl runs outside
// ShaderToy. The converter PREFERS the canonical `mainImage` (our harness generates its own fullscreen
// pass) and DROPS the `void main()` wrapper with a Warning. The dropped wrapper's `mainImage` translation
// must be byte-identical to the same shader WITHOUT the wrapper (see gradient_uv.glsl shape).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.0, 1.0);
}

void main()
{
    mainImage(gl_FragColor, gl_FragCoord.xy);
}
