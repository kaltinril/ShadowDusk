// Exercises: vector swizzles (.xy / .yx / .rgb / .bgr) and swizzle on the write side.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec4 c;
    c.xy = uv;
    c.zw = uv.yx;
    vec3 rgb = c.rgb;
    vec3 swapped = rgb.bgr;
    fragColor = vec4(swapped, 1.0);
}
