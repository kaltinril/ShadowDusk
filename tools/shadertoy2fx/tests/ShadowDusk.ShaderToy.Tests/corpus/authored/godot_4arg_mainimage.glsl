// Phase 46: the GdShaders / Godot 4 alternate `mainImage` signature
// `void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)`. `uv` is Godot's normalized
// SCREEN_UV ([0,1]) the harness sets from fragCoord/iResolution; `inputColor` is the iChannel0 (screen
// texture) sample at uv; `outputColor` is the returned fragment color. (The `in` qualifiers may be
// `const in` or omitted; here they are explicit `in`.)
void mainImage(in vec4 inputColor, in vec2 uv, out vec4 outputColor)
{
    // A simple tint of the input color modulated by the screen UV.
    vec3 tint = vec3(uv.x, uv.y, 0.5);
    outputColor = vec4(inputColor.rgb * 0.5 + tint * 0.5, 1.0);
}
