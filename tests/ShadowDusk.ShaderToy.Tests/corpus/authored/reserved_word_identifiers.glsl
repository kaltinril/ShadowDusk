// F1 (Phase 47): identifiers that are HLSL reserved keywords (matrix, sample) but valid GLSL names, and
// a helper function whose name is a reserved keyword (linear). Each is auto-renamed so the converted .fx
// compiles.
float linear(float t) { return t * t; }
void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
  vec2 matrix = fragCoord / iResolution.xy;
  float sample = linear(matrix.x) + linear(matrix.y);
  fragColor = vec4(matrix, sample, 1.0);
}
