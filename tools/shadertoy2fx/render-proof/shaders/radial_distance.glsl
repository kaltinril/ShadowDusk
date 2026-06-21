// Exercises: a deterministic radial field analytic at any iResolution (no time dependence).
// Centered UV in [-1,1]; output.r = distance from center (0 at center, ~sqrt(2) at corners,
// clamped to 1), output.g = 1 - r. This gives analytic, time-independent expected pixels:
//   center  -> r=0     -> (R,G) = (0, 1)
//   corners -> r>=1    -> (R,G) = (1, 0)
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;       // [0,1]
    vec2 centered = uv * 2.0 - 1.0;             // [-1,1], origin at center
    float r = clamp(length(centered), 0.0, 1.0);
    fragColor = vec4(r, 1.0 - r, 0.0, 1.0);
}
