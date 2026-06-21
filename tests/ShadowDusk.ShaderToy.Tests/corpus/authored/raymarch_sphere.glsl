// Exercises: a small sphere+plane raymarcher - a bounded for-loop marching along a ray, an
// analytic surface normal via central differences, diffuse + Blinn-style specular lighting, and
// iTime camera orbit. Stresses loops, normalize/dot/reflect, length, and helper-call ordering.
float sdSphere(vec3 p, float r)
{
    return length(p) - r;
}

// Scene SDF: a unit sphere bobbing above an infinite ground plane at y = -1.
float map(vec3 p)
{
    float sphere = sdSphere(p - vec3(0.0, 0.2 * sin(iTime), 0.0), 1.0);
    float plane = p.y + 1.0;
    return min(sphere, plane);
}

vec3 calcNormal(vec3 p)
{
    vec2 e = vec2(0.001, 0.0);
    return normalize(vec3(
        map(p + e.xyy) - map(p - e.xyy),
        map(p + e.yxy) - map(p - e.yxy),
        map(p + e.yyx) - map(p - e.yyx)));
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;

    // Camera orbits the origin on a circle in the XZ plane.
    float ca = iTime * 0.5;
    vec3 ro = vec3(3.0 * sin(ca), 1.0, 3.0 * cos(ca));
    vec3 ta = vec3(0.0, 0.0, 0.0);
    vec3 fwd = normalize(ta - ro);
    vec3 right = normalize(cross(vec3(0.0, 1.0, 0.0), fwd));
    vec3 up = cross(fwd, right);
    vec3 rd = normalize(uv.x * right + uv.y * up + 1.5 * fwd);

    // March.
    float t = 0.0;
    float d = 0.0;
    bool hit = false;
    for (int i = 0; i < 64; i++)
    {
        vec3 p = ro + rd * t;
        d = map(p);
        if (d < 0.001)
        {
            hit = true;
            break;
        }
        t += d;
        if (t > 20.0)
        {
            break;
        }
    }

    vec3 col = vec3(0.5, 0.65, 0.9) - 0.3 * uv.y; // sky gradient
    if (hit)
    {
        vec3 p = ro + rd * t;
        vec3 n = calcNormal(p);
        vec3 lightDir = normalize(vec3(0.6, 0.8, -0.4));
        float diff = max(dot(n, lightDir), 0.0);
        vec3 h = normalize(lightDir - rd);
        float spec = pow(max(dot(n, h), 0.0), 32.0);
        vec3 base = vec3(0.85, 0.45, 0.30);
        col = base * (0.15 + 0.85 * diff) + vec3(1.0) * spec * 0.6;
    }

    col = pow(col, vec3(0.4545)); // gamma
    fragColor = vec4(col, 1.0);
}
