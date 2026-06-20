// Exercises: if / else if / else control flow selecting between color bands.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec3 col;
    if (uv.x < 0.33)
    {
        col = vec3(1.0, 0.0, 0.0);
    }
    else if (uv.x < 0.66)
    {
        col = vec3(0.0, 1.0, 0.0);
    }
    else
    {
        col = vec3(0.0, 0.0, 1.0);
    }
    fragColor = vec4(col, 1.0);
}
