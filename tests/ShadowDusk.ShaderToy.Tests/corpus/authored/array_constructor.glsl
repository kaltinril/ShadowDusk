// Exercises (G7): the GLSL array-constructor expression in both the unsized `T[](...)` and sized
// `T[N](...)` forms, used as a declaration initializer. Each becomes an HLSL brace list `{ ... }`.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;

    float a[3] = float[](0.2, 0.5, 0.3);
    vec3 b[2] = vec3[2](vec3(1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0));

    float w = a[0] * uv.x + a[1] * uv.y + a[2];
    vec3 col = mix(b[0], b[1], w);
    fragColor = vec4(col, 1.0);
}
