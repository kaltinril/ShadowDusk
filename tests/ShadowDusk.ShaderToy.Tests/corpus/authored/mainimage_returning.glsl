// The desktop-runner "returning" mainImage form: vec3 mainImage(in vec2 fragCoord) RETURNS the color
// (the file's own void main() assigns it to gl_FragColor). The harness calls
// `rgb = mainImage(fragCoord)` and returns float4(rgb, 1.0). A faithful single-pass entry shape.
vec3 mainImage(in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    return vec3(uv.x, uv.y, 0.5 + 0.5 * cos(iTime));
}

void main()
{
    gl_FragColor = vec4(mainImage(gl_FragCoord.xy), 1.0);
}
