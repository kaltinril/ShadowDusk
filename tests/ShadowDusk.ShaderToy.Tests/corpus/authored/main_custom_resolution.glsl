// G2 plain-GLSL entry mode with a declared custom `resolution` uniform (glslViewer / Bonzomatic
// style) + gl_FragCoord. The vec2 `resolution` does not exact-type-match the vec3 iResolution
// built-in, so it is exposed as a custom effect parameter the host drives (reported in UsedUniforms).
uniform vec2 resolution;

void main()
{
    vec2 uv = gl_FragCoord.xy / resolution;
    float d = length(uv - 0.5);
    gl_FragColor = vec4(d, 1.0 - d, 0.0, 1.0);
}
