// Reject: a custom uniform of an unsupported sampler kind (sampler3D). Only sampler2D is supported;
// sampler3D / samplerCube are a loud reject.
uniform sampler3D uVolume;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.0, 1.0);
}
