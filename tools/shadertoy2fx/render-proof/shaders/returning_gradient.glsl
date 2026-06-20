// Exercises the desktop-runner "returning" mainImage form: vec3 mainImage(in vec2 fragCoord) RETURNS
// the color (the file's own void main() assigns it). The harness calls `rgb = mainImage(fragCoord)`
// and returns float4(rgb, 1.0). This must render the SAME analytic gradient + SAME Y-orientation as
// gradient_uv (the standard out-vec4 form), proving the returning-form harness wires fragCoord/return
// correctly.
vec3 mainImage(in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    return vec3(uv.x, uv.y, 0.5);
}

void main()
{
    gl_FragColor = vec4(mainImage(gl_FragCoord.xy), 1.0);
}
