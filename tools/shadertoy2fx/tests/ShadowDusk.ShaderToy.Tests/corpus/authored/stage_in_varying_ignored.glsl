// Phase 46: a top-level `in`/`varying`/`attribute` declaration is web/desktop-export VERTEX-STAGE
// leftover. The converter IGNORES the declaration (does not reject, does not emit a parameter). When
// the varying is a conventional fullscreen screen-coordinate name (here `vUv`), a reference to it
// resolves to the harness normalized screen UV ([0,1], ShaderToy bottom-left origin). The plain
// `varying vec3 vUnusedNormal;` below is ignored and never referenced, so it just vanishes.
varying vec2 vUv;
varying vec3 vUnusedNormal;
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // vUv is the fullscreen UV in [0,1]; it aliases fragCoord / iResolution.xy.
    fragColor = vec4(vUv, 0.5, 1.0);
}
