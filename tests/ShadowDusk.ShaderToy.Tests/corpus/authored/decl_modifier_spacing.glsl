// Exercises B5: a declaration whose storage / precision modifier sits AFTER the type spelling (a
// copied-declaration token-spacing edge case). GLSL precision qualifiers and a stray `const` in a
// post-type position must be dropped so the emitted HLSL is a clean `type name`, never
// `type modifier name` which the stricter HLSL compilers reject as "modifiers must appear before
// type".
float scale(in float v)
{
    float const k = 2.0;   // stray modifier after the type
    return v * k;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 mediump uv = fragCoord / iResolution.xy;   // stray precision qualifier after the type
    float s = scale(uv.x);
    fragColor = vec4(s, uv.y, 0.0, 1.0);
}
