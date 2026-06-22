// Real-world ShaderToy shader (CC0): "Abstract Waterfall" by mrange, https://www.shadertoy.com/view/s32GDD
//
// Primary regression for MULTI-DECLARATOR const GLOBALS: the file opens with
//   const float
//     PI  =3.141592654
//   , TAU =2.*PI
//   , PI_2=.5*PI
//   ;
// a single `const` declaration with three comma-separated declarators, where a later one (TAU) references
// an earlier one (PI). The const-global parser used to swallow the commas as the sequence operator, so only
// PI was registered and TAU/PI_2 surfaced as a misleading "Undeclared identifier 'TAU'". Each declarator is
// now its own const global in source order. It also exercises fwidth, tanh, and atan(y, x). Stored
// faithfully (shader body unedited); only this header comment was added.

const float
  PI  =3.141592654
, TAU =2.*PI
, PI_2=.5*PI
;

// License: MIT, author: Inigo Quilez, found: https://iquilezles.org/articles/intersectors/
float ray_issphere4(vec3 ro, vec3 rd, float ra) {
  float
    r2 = ra*ra
  ;

  vec3
    d2 = rd*rd
  , d3 = d2*rd
  , o2 = ro*ro
  , o3 = o2*ro
  ;

  float
    ka = 1./dot(d2,d2)
  , k3 = ka* dot(ro,d3)
  , k2 = ka* dot(o2,d2)
  , k1 = ka* dot(o3,rd)
  , k0 = ka*(dot(o2,o2) - r2*r2)
  , c2 = k2 - k3*k3
  , c1 = k1 + 2.*k3*k3*k3 - 3.*k3*k2
  , c0 = k0 - 3.*k3*k3*k3*k3 + 6.*k3*k3*k2 - 4.*k3*k1
  , p = c2*c2 + c0/3.
  , q = c2*c2*c2 - c2*c0 + c1*c1
  , h = q*q - p*p*p
  ;

  if (h<0.) return -1.;

  float
    sh= sqrt(h)
  , s = sign(q+sh)*pow(abs(q+sh),1./3.)
  , t = sign(q-sh)*pow(abs(q-sh),1./3.)
  ;

  vec2
    w = vec2( s+t,s-t )
  , v = vec2( w.x+c2*4.0, w.y*sqrt(3.0) )*0.5
  ;

  float
    r = length(v)
  ;

  return abs(v.y)/sqrt(r+v.x) - c1/r - k3;

}

// License: Unknown, author: Unknown, found: don't remember
float hash(vec2 co) {
  return fract(sin(dot(co.xy ,vec2(12.9898,58.233))) * 13758.5453);
}

// License: Unknown, author: Unknown, found: don't remember
float hash(float co) {
  return fract(sin(co*12.9898) * 13758.5453);
}

float dot2(vec2 p) {
  return dot(p,p);
}

vec3 lines(vec3 F, vec3 P, float A, float AA, float T) {
  const float
    N=4.
  ;
  const vec2
    Z0=vec2(5e-3,TAU/6.)
  , Z1=.2*Z0
  ;

  for(float j=0.;j<N;++j) {
    vec3
      p=P
    , b
    ;
    p.x+=Z0.x*j/N;
    float
      h0=hash(vec2(floor(p.x/Z0.x+.5),j))
    , h1=fract(8667.0*h0)
    , a=A-T*(1.+h0)
    ;
    vec2
      q=vec2(p.x,a)
    , n=floor(vec2(p.x,a)/Z0+.5)
    , q0
    , q1
    , d
    ;
    q-=n*Z0;
    q0=q-vec2(0,Z1.y);
    q1=q;
    q1.y=max(abs(q1.y)-Z1.y,0.);

    d=vec2(length(q0),length(q1))-Z1.x;
    vec4
      c=.5*(1.+sin(.1*T+TAU*h1+vec4(0,1,8,4)));
    b=c.xyz*c.w;
    F+=exp(19.*(q.y-Z1.y))*smoothstep(AA,-AA,d.y)*b;
    F+=smoothstep(AA,-AA,d.x-.5*Z1.x)*sqrt(.5*(b-min(0.,d.x-.5*Z1.x)));
  }
  return F;
}

vec3 stars(vec3 F, vec3 P, float A, float AA, float T) {
  const vec2
    Z0=vec2(.1,TAU/6.)
  ;
  vec3
    p=P
    ;
  p.x+=.123;
  float
    h0=hash(vec2(floor(p.x/Z0.x+.5),0))
  , h1=fract(8667.*h0)
  , a=A-T*(1.5+h0)
  ;
  vec2
    q=vec2(p.x,a)
  , n=floor(vec2(p.x,a)/Z0+.5)
  , dq
  ;
  q-=n*Z0;
  dq=fwidth(q);
  vec3
    c=.5*(1.+sin(7.*T+TAU*h1+vec3(2,1,0)))*hash(.123*n.x+floor(T*121.))
  ;
  F+=c.xyz*7e-6/max(dot2(vec2(dq.y/dq.x,1)*q),1e-6);
  return F;
}

void mainImage(out vec4 O, vec2 C) {
  vec2
    R=iResolution.xy
  ;
  vec3
    RO=vec3(0.,-1.9,-0)
  , RD=normalize(vec3(C-.5*R, sqrt(2.)*R.y))
  , F=3e-4*vec3(1,4,40)/(dot2(vec2(sqrt(.5),1).yx*((C+C-R)/R.x-vec2(0,R.y/R.x)))+2e-3)
  , P
  ;

  float
    Z=ray_issphere4(RO,RD,2.)
  , A
  , AA
  , T=.07*iTime+123.
  ;

  P=Z*RD+RO;

  A=atan(P.z,P.y);
  AA=sqrt(2.)*length(fwidth(vec2(P.x,A)));
  F=lines(F,P,A,AA,T);
  F=stars(F,P,A,AA,T);
  F*=4.;
  F-=2e-2*vec3(2,3,1);
  F=tanh(F);
  F=max(F,0.);
  F=sqrt(F)-.05;
  O=vec4(F,1);
}
