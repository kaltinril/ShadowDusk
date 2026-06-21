// Reject: a custom uniform of an unsupported type (non-square mat2x3). Custom uniforms must be a
// supported scalar/vector/matrix (mat2/3/4) or sampler2D; everything else is a loud reject.
uniform mat2x3 uXform;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.0, 1.0);
}
