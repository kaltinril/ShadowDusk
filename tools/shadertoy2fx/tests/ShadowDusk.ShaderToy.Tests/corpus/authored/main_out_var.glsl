// G2 plain-GLSL entry mode: a user-declared `out vec4 <name>;` fragment output (GLSL ES 3.00 / 330)
// instead of the legacy gl_FragColor. The declared output is CONSUMED (not emitted as a global /
// parameter) and becomes the synthesized PS's COLOR0 return. Also exercises gl_FragCoord + a helper.
out vec4 outColor;

vec3 tint(vec2 uv)
{
    return vec3(uv, 0.5 + 0.5 * uv.x);
}

void main()
{
    vec2 uv = gl_FragCoord.xy / iResolution.xy;
    outColor = vec4(tint(uv), 1.0);
}
