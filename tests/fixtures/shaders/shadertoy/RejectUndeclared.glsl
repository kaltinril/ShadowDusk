// A ShaderToy shader that references an undeclared, host-specific identifier (RENDERSIZE is an ISF
// built-in, not a ShaderToy one). The converter must reject this LOUDLY with a located diagnostic
// rather than guess a uniform — exercised by the CLI error-path integration test (Phase 47).
void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    fragColor = vec4(RENDERSIZE, 0.0, 1.0);
}
