// Exercises: iTime animation driving a pulsing color via sin/cos.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float pulse = 0.5 + 0.5 * sin(iTime + uv.x * 6.2831853);
    float wave  = 0.5 + 0.5 * cos(iTime * 2.0 + uv.y * 6.2831853);
    fragColor = vec4(pulse, wave, 0.5 * (pulse + wave), 1.0);
}
