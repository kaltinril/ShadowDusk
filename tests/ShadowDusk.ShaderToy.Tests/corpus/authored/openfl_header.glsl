// Phase 46: an OpenFL / Haxe fullscreen-filter export. The `#pragma header` line (OpenFL injects its
// own GLSL header there) is stripped like any `#pragma`. The two OpenFL fullscreen globals are mapped
// by their conventional meaning: `openfl_TextureCoordv` (vec2) -> the harness normalized screen UV
// ([0,1]); `openfl_TextureSize` (vec2) -> the resolution (iResolution.xy).
#pragma header
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = openfl_TextureCoordv;
    vec2 res = openfl_TextureSize;
    fragColor = vec4(uv, res.x / max(res.y, 1.0), 1.0);
}
