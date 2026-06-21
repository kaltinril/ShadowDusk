// Exercises the redundant `uniform sampler2D iChannelN;` redeclaration: it is already a built-in
// channel, so the declaration is accepted-and-ignored (dropped), letting the shader convert.
uniform sampler2D iChannel0;
uniform vec3 iResolution;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = texture(iChannel0, uv);
}
