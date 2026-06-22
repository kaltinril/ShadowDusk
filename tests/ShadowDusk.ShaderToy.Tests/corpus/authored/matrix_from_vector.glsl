// A matrix constructor whose single VECTOR argument supplies all NxN components (GLSL flattens the
// vector column-major). mat2(vec4) is the common code-golf rotation form
// mat2(cos(a + vec4(0, 33, 11, 0))). The converter passes the vector straight through to HLSL's
// floatNxN(...) (which flattens it the same way), consistent with the matrix-order trap.
void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
  vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
  float a = iTime;
  mat2 rot = mat2(cos(a + vec4(0.0, 33.0, 11.0, 0.0)));
  uv = rot * uv;
  fragColor = vec4(0.5 + 0.5 * uv, 0.5, 1.0);
}
