// CLI integration fixture (Phase 47 F1): a local that shadows a called function. The CLI must convert
// (auto-renaming with a warning) and compile it to a loadable effect rather than failing on generated HLSL.
mat3 rot(vec3 d, vec3 z) {
  vec3 v = cross(z, d);
  return mat3(v.x, v.y, v.z, d.x, d.y, d.z, z.x, z.y, z.z);
}
void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
  vec2 p = fragCoord / iResolution.xy;
  mat3 rot = rot(normalize(vec3(p, 1.0)), normalize(vec3(1.0, p)));
  vec3 col = rot * vec3(p, 1.0);
  fragColor = vec4(col, 1.0);
}
