// MUST REJECT: an unsized / runtime-sized array (`float data[];`), which is outside the v1 subset.
// A fixed-size array (`float k[3];`) is now supported (G7); an unsized one has no fixed length.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float data[];
    fragColor = vec4(uv, 0.0, 1.0);
}
