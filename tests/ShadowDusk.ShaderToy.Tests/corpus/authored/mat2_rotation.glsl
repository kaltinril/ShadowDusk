// Exercises: the MATRIX TRAP - a 2x2 rotation via mat2 * vec2 (column-major M*v must become mul(v,M)).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    float a = iTime;
    mat2 rot = mat2(cos(a), -sin(a),
                    sin(a),  cos(a));
    vec2 p = rot * uv;
    float stripes = 0.5 + 0.5 * sin(p.x * 20.0);
    fragColor = vec4(stripes, stripes, stripes, 1.0);
}
