// Exercises: atan(y,x) two-arg form (-> atan2) for polar angle, plus length for radius.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float angle = atan(p.y, p.x);
    float radius = length(p);
    float spiral = 0.5 + 0.5 * sin(angle * 6.0 + radius * 20.0 - iTime * 3.0);
    fragColor = vec4(spiral, spiral * 0.5, 1.0 - spiral, 1.0);
}
