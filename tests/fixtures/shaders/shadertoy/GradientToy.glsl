// Minimal single-pass ShaderToy "image" shader: a UV gradient.
// Used by the CLI ShaderToy/GLSL-input integration tests (Phase 47).
void mainImage( out vec4 fragColor, in vec2 fragCoord )
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.0, 1.0);
}
