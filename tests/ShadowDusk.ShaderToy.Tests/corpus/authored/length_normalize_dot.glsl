// Exercises: length / normalize / dot vector intrinsics for a simple lambert-style shading dot.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    vec3 n = normalize(vec3(p, 1.0));
    vec3 lightDir = normalize(vec3(0.5, 0.5, 1.0));
    float diff = max(dot(n, lightDir), 0.0);
    float r = length(p);
    fragColor = vec4(vec3(diff) * (1.0 - r), 1.0);
}
