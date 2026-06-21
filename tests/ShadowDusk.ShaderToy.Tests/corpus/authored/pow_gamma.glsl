// Exercises: pow(), abs(), and a vec3 gamma curve plus compound *= assignment.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec3 col = vec3(uv.x, uv.y, abs(0.5 - uv.x));
    col = pow(col, vec3(1.0 / 2.2));
    col *= 0.9;
    fragColor = vec4(col, 1.0);
}
