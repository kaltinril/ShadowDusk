// Exercises (G5): harmless preprocessor directives at the top of a plain-GLSL/glslViewer shader.
// `#version`, `#extension`, `#pragma`, `#line`, and a glslViewer/Bonzomatic channel-binding metadata
// directive (`#iChannel0 "..."`) are all silently ignored (dropped, not rejected). `#include` would
// still be a loud reject; none appears here.
#version 330 core
#extension GL_OES_standard_derivatives : enable
#pragma optimize(on)
#iChannel0 "https://example.com/tex.png"

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec3 col = 0.5 + 0.5 * cos(iTime + uv.xyx + vec3(0.0, 2.0, 4.0));
    fragColor = vec4(col, 1.0);
}
