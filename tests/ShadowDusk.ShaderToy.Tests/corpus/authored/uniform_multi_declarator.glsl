// Exercises a multi-declarator uniform comma list (`uniform float a, b, c;`): each declarator
// becomes its own custom uniform (an effect parameter the consumer drives), AND a redundant
// built-in re-declaration WITH an initializer is dropped (the built-in is injected; the value is
// irrelevant).
uniform float uA, uB, uC;
uniform vec3 iResolution = vec3(1920.0, 1080.0, 1.0);

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float v = uA * uv.x + uB * uv.y + uC;
    fragColor = vec4(v, v, v, 1.0);
}
