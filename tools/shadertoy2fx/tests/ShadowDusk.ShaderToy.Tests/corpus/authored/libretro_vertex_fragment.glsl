// Phase 46: the libretro / RetroArch ".slang" stage-split shape — one file wraps BOTH stages in
// `#if defined(VERTEX) ... #elif defined(FRAGMENT) ... #endif`. The build system #defines one of
// VERTEX / FRAGMENT per compilation. Our converter only needs the FRAGMENT stage (the harness
// synthesizes its own vertex shader), so when neither symbol is otherwise defined the converter seeds
// FRAGMENT=1 / VERTEX=0 so the fragment branch (the real mainImage) survives. The VERTEX branch is
// dropped (it would otherwise be the only branch kept, producing a "no entry point" reject).
#if defined(VERTEX)
// Vertex stage (ignored by the converter): a passthrough fullscreen vertex shader would live here.
attribute vec2 aPosition;
void main_vertex_unused()
{
    // (stripped — the VERTEX branch is inactive)
}
#elif defined(FRAGMENT)
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.25, 1.0);
}
#endif
