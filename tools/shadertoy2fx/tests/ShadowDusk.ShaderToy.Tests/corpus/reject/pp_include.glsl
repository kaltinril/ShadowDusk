// Must be REJECTED (G5 boundary): `#include` stays a loud reject (there is no file resolver). While
// #version/#extension/#pragma and glslViewer channel-metadata directives are now silently ignored,
// #include cannot be silently dropped (it would lose code), so it remains a located reject.
#include "common.glsl"

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = vec4(fragCoord / iResolution.xy, 0.0, 1.0);
}
