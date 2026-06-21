// Reject case for L1 (honesty): a free identifier that is NOT a known ShaderToy built-in, a local,
// a const global, or a user function must be a CLEAN convert-time Error (with line/col), never a
// silent pass-through that only fails later in the HLSL compiler as "use of undeclared identifier".
// `RENDERSIZE` is an ISF builtin, not a ShaderToy uniform, so it cannot be supplied.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / RENDERSIZE;
    fragColor = vec4(uv, 0.5, 1.0);
}
