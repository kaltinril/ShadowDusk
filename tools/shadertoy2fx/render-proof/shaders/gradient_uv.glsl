// Exercises: basic fragCoord/iResolution normalization into a UV gradient.
// This is the Y-orientation oracle: with ShaderToy's bottom-left fragCoord origin,
// the bottom-left pixel is (R,G)=(0,0) and the top-right pixel is (R,G)=(1,1).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv.x, uv.y, 0.5, 1.0);
}
