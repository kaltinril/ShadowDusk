// Exercises: texture(iChannel0, uv) sampling -> must become Texture2D.Sample / tex2D.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec4 tex = texture(iChannel0, uv);
    fragColor = vec4(tex.rgb, 1.0);
}
