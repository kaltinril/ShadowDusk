// A mainImage PROTOTYPE (forward declaration) plus its definition and a desktop-runner main()
// wrapper. The prototype must NOT count as a duplicate definition; ShaderToy mode is used and the
// wrapper is dropped. (Common third-party desktop-export shape.)
void mainImage(out vec4 fragColor, in vec2 fragCoord);

void main()
{
    mainImage(gl_FragColor, gl_FragCoord.xy);
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    fragColor = vec4(uv, 0.5 + 0.5 * sin(iTime), 1.0);
}
