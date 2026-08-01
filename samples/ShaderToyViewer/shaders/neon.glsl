// Originally from: https://www.shadertoy.com/view/WlByzy
// License CC0: Neonwave style road, sun and city
//  The result of a bit of experimenting with neonwave style colors.
//
// === ShadowDusk Phase 46 trim notes (see LICENSES.md) ===
// Trimmed to the v1 ShaderToy->FX subset. Changes from the original CC0 source:
//   * `mod1(inout float p, ...)` (which both wrote the wrapped coord into p AND returned the
//     cell index) split into two pure helpers `mod1p` (wrapped coord) and `mod1cell` (cell
//     index), since `inout` is not in the v1 subset. Callers updated to use each output.
//   * The `dFdx`-based antialias width in `groundEffect` replaced with a small
//     screen-space constant `aa` (v1 subset has no derivative intrinsics). Visual
//     effect is near-identical; the grid edges are a hair less crisp.
// No other logic changed.

#define PI          3.141592654
#define TAU         (2.0*PI)

#define TIME        iTime
#define RESOLUTION  iResolution

vec3 hsv2rgb(vec3 c) {
  const vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
  return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

float hash(in float co) {
  return fract(sin(co*12.9898) * 13758.5453);
}

float hash(in vec2 co) {
  return fract(sin(dot(co.xy ,vec2(12.9898,58.233))) * 13758.5453);
}

float psin(float a) {
  return 0.5 + 0.5*sin(a);
}

// Trim: original was `float mod1(inout float p, float size)` which BOTH mutated p (to the
// wrapped coordinate) AND returned the cell index `c`. `inout` is not in the v1 subset, so it is
// split into two pure helpers preserving both outputs:
//   mod1p(p, size)    -> the wrapped coordinate (what the original wrote back into p)
//   mod1cell(p, size) -> the cell index (what the original returned)
float mod1p(float p, float size) {
  float halfsize = size*0.5;
  return mod(p + halfsize, size) - halfsize;
}

float mod1cell(float p, float size) {
  float halfsize = size*0.5;
  return floor((p + halfsize)/size);
}

float circle(vec2 p, float r) {
  return length(p) - r;
}

float box(vec2 p, vec2 b) {
  vec2 d = abs(p)-b;
  return length(max(d,0.0)) + min(max(d.x,d.y),0.0);
}

float planex(vec2 p, float w) {
  return abs(p.y) - w;
}

float planey(vec2 p, float w) {
  return abs(p.x) - w;
}

float pmin(float a, float b, float k) {
  float h = clamp( 0.5+0.5*(b-a)/k, 0.0, 1.0 );
  return mix( b, a, h ) - k*h*(1.0-h);
}

float pmax(float a, float b, float k) {
  return -pmin(-a, -b, k);
}

float sun(vec2 p) {
  const float ch = 0.0125;
  vec2 sp = p;
  vec2 cp = p;
  cp.y = mod1p(cp.y, ch*6.0);

  float d0 = circle(sp, 0.5);
  float d1 = planex(cp, ch);
  float d2 = p.y+ch*3.0;

  float d = d0;
  d = pmax(d, -max(d1, d2), ch*2.0);

  return d;
}

float city(vec2 p) {
  float sd = circle(p, 0.5);
  float cd = 1E6;

  const float count = 5.0;
  const float width = 0.1;

  for (float i = 0.0; i < count; ++i) {
    vec2 pp = p;
    pp.x += i*width/count;
    float nn = mod1cell(pp.x, width);   // original mod1 return value (cell index)
    pp.x = mod1p(pp.x, width);          // original mod1 in-place write (wrapped coord)
    float rr = hash(nn+sqrt(3.0)*i);
    float dd = box(pp-vec2(0.0, -0.5), vec2(0.02, 0.35*(1.0-smoothstep(0.0, 5.0, abs(nn)))*rr+0.1));
    cd = min(cd, dd);
  }

  return max(sd,cd);
}

vec3 sunEffect(vec2 p) {
  vec3 col = vec3(0.1);
  vec3 skyCol1 = hsv2rgb(vec3(283.0/360.0, 0.83, 0.16));
  vec3 skyCol2 = hsv2rgb(vec3(297.0/360.0, 0.79, 0.43));
  col = mix(skyCol1, skyCol2, pow(clamp(0.5*(1.0+p.y+0.1*sin(4.0*p.x+TIME*0.5)), 0.0, 1.0), 4.0));

  p.y -= 0.375;
  float ds = sun(p);
  float dc = city(p);

  float dd = circle(p, 0.5);

  vec3 sunCol = mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 1.0), clamp(0.5 - 1.0*p.y, 0.0, 1.0));
  vec3 glareCol = sqrt(sunCol);

  col += glareCol*(exp(-30.0*ds))*step(0.0, ds);

  float aa = 4.0 / RESOLUTION.y;
  float t1 = smoothstep(0.0, 0.075, -dd);
  float t2 = smoothstep(0.0, 0.3, -dd);
  col = mix(col, sunCol, smoothstep(-aa, 0.0, -ds));
  col = mix(col, glareCol, smoothstep(-aa, 0.0, -dc)*t1);
  col += vec3(0.0, 0.25, 0.0)*(exp(-90.0*dc))*step(0.0, dc)*t2;

  return col;
}

float ground(vec2 p) {
  p.y += TIME*80.0;
  p *= 0.075;
  vec2 gp = p;
  gp = fract(gp) - vec2(0.5);
  float d0 = abs(gp.x);
  float d1 = abs(gp.y);

  const float rw = 2.5;
  const float sw = 0.0125;

  vec2 rp = p;
  rp.y = mod1p(rp.y, 12.0);
  float d3 = abs(rp.x) - rw;
  float d4 = abs(d3) - sw*2.0;
  float d5 = box(rp, vec2(sw*2.0, 2.0));
  vec2 sp = p;
  sp.y = mod1p(sp.y, 4.0);
  sp.x = abs(sp.x);
  sp -= vec2(rw - 0.125, 0.0);
  float d6 = box(sp, vec2(sw, 1.0));

  float d = d0;
  d = pmin(d, d1, 0.1);
  d = max(d, -d3);
  d = min(d, d4);
  d = min(d, d5);
  d = min(d, d6);

  return d;
}

vec3 groundEffect(vec2 p) {
  vec3 ro = vec3(0.0, 20.0, 0.0);
  vec3 ww = normalize(vec3(0.0, -0.025, 1.0));
  vec3 uu = normalize(cross(vec3(0.0,1.0,0.0), ww));
  vec3 vv = normalize(cross(ww,uu));
  vec3 rd = normalize(p.x*uu + p.y*vv + 2.5*ww);

  float distg = (-9.0 - ro.y)/rd.y;

  const vec3 shineCol = 0.75*vec3(0.5, 0.75, 1.0);
  const vec3 gridCol = vec3(1.0);

  vec3 col = vec3(0.0);
  if (distg > 0.0) {
    vec3 pg = ro + rd*distg;
    // Trim: original used `length(dFdx(pg))*0.0002*RESOLUTION.x` for AA width.
    // v1 has no derivatives; use a small constant instead.
    float aa = 0.0025;

    float dg = ground(pg.xz);

    col = mix(col, gridCol, smoothstep(-aa, 0.0, -(dg+0.0175)));
    col += shineCol*(exp(-10.0*clamp(dg, 0.0, 1.0)));
    col = clamp(col, 0.0, 1.0);

    col *= pow(1.0-smoothstep(ro.y*3.0, 220.0+ro.y*2.0, distg), 2.0);
  }

  return col;
}

vec3 postProcess(vec3 col, vec2 q)  {
  col = clamp(col,0.0,1.0);
  col=col*0.6+0.4*col*col*(3.0-2.0*col);
  col=mix(col, vec3(dot(col, vec3(0.33))), -0.4);
  col*=0.5+0.5*pow(19.0*q.x*q.y*(1.0-q.x)*(1.0-q.y),0.7);
  return col;
}

vec3 effect(vec2 p, vec2 q) {
  vec3 col = vec3(0.0);

  vec2 off = vec2(0.0, 0.0);

  col += sunEffect(p+off);
  col += groundEffect(p+off);

  col = postProcess(col, q);
  return col;
}

void mainImage(out vec4 fragColor, vec2 fragCoord) {
  vec2 q = fragCoord/iResolution.xy;
  vec2 p = -1. + 2. * q;
  p.x *= RESOLUTION.x / RESOLUTION.y;

  vec3 col = effect(p, q);

  fragColor = vec4(col, 1.0);
}
