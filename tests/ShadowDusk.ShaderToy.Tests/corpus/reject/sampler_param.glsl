// A sampler2D FUNCTION PARAMETER is valid HLSL but does not compile through the ShadowDusk legacy-FX9
// -> GL/DX pipeline (a sampler cannot be passed as a function argument there). Out of scope: loud,
// named reject (the same class of limit as the mip-bias texture reject).
vec4 sampleTex(sampler2D tex, vec2 uv) { return texture(tex, uv); }
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = sampleTex(iChannel0, fragCoord / iResolution.xy);
}
