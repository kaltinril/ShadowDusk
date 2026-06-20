// Render-proof: a custom uniform (uColor) the HOST drives. The whole image is the host-supplied
// color, proving a consumer-set effect parameter reflects through to a real rendered pixel.
uniform vec3 uColor;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = vec4(uColor, 1.0);
}
