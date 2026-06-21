// Exercises: top-level custom uniforms (a scalar and a vector) driven into mainImage.
// Each is exposed as an HLSL effect-parameter global the consumer sets every frame.
uniform float uIntensity;
uniform vec3  uTint;

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    vec3 col = uTint * (uv.x + uv.y) * 0.5 * uIntensity;
    fragColor = vec4(col, 1.0);
}
