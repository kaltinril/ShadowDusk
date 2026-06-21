// MUST REJECT: a top-level `in`/`varying` of a NON-coordinate / unknown name is IGNORED (the harness
// has no per-vertex value for it), but here the body actually REFERENCES it. We cannot invent its
// value, so the reference is a loud, located "undeclared identifier" reject. (A conventional
// coordinate-varying name like vUv/texCoord/uv WOULD resolve to the harness screen UV instead — see
// authored/stage_in_varying_ignored.glsl.)
in vec3 vWorldNormal;
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    // vWorldNormal is vertex-stage data the converter dropped; referencing it must reject loudly.
    fragColor = vec4(normalize(vWorldNormal) * 0.5 + 0.5, 1.0);
}
