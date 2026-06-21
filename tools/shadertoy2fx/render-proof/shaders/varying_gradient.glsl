// Phase 46 (second batch): exercises the IGNORED stage-I/O coordinate varying -> harness screen-UV
// alias. `vUv` is a top-level `varying vec2` (vertex-stage leftover the converter ignores) referenced
// as the fullscreen UV; it resolves to the harness normalized screen UV (fragCoord / iResolution.xy).
// This must render with the SAME orientation as gradient_uv (bottom-left = (R,G)=(0,0), top-right =
// (1,1)) — a green run proves the UV-alias heuristic is not upside-down.
varying vec2 vUv;
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    fragColor = vec4(vUv.x, vUv.y, 0.5, 1.0);
}
