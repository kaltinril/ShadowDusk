// Exercises: a bounded for-loop accumulating into a float (loop translation + compound assignment).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float sum = 0.0;
    for (int i = 0; i < 6; i++)
    {
        float fi = float(i) + 1.0;
        sum += sin(p.x * fi + iTime) / fi;
    }
    float v = 0.5 + 0.5 * sum;
    fragColor = vec4(v, v, v, 1.0);
}
