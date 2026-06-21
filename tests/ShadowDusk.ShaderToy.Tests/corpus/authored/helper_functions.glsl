// Exercises: user-defined helper functions called from mainImage (declaration ordering / signature translation).
float circle(vec2 p, float r)
{
    return 1.0 - smoothstep(r - 0.01, r + 0.01, length(p));
}

vec3 tint(vec3 base, float k)
{
    return base * k;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 p = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float c = circle(p, 0.3);
    vec3 col = tint(vec3(0.2, 0.7, 1.0), c);
    fragColor = vec4(col, 1.0);
}
