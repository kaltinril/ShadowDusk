// Exercises: a custom sampler2D uniform sampled with texture(...) -> tex2D, emitted as the same
// texture + sampler_state pair used for iChannelN.
uniform sampler2D uNoise;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec4 n = texture(uNoise, uv);
    fragColor = vec4(n.rgb, 1.0);
}
