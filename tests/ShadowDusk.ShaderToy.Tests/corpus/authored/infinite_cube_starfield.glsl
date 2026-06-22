// Real-world ShaderToy raymarcher (CC0): chromatic-aberration "infinite cube" starfield,
// https://www.shadertoy.com/view/fXlSDf  (techniques credited to byt3_m3chanic, FabriceNeyrat2, iq,
// shane, XorDev, and others).
//
// Primary regression for the LEGACY HLSL FOR-SCOPE LEAK: renderScene() reuses the loop variable `i`
// across four sibling/nested for-loops (two `int`, two `float`). GLSL scopes each loop's variable to its
// own loop, so the reuse is valid; HLSL leaks the for-init into the enclosing scope, so DXC rejects the
// reuse as a -Wfor-redefinition error (under -WX) regardless of type. The converter keeps the first
// loop's `i` and renames the later ones (i_sd, i_sd2, i_sd3), each scoped to its own loop.
//
// It also exercises, in one shader: `inout` parameters (M/Q), the iMouse uniform, a vector-valued
// ternary (faceUV), compound-assign with a matrix (p.xz *= rot(...)), and a mat2(c,-s,s,c) rotation.
// Stored faithfully (shader body unedited); only this header comment was added.

#define PI  3.14159265359
#define NUM_LAYERS 4.0

// === HELPER FUNCTIONS ===
mat2 rot(float a) {
    float c = cos(a), s = sin(a);
    return mat2(c, -s, s, c);
}

mat2 Rot(float a) { return rot(a); }

vec2 N(float angle) {
    return vec2(sin(angle), cos(angle));
}

vec3 palette(float t) {
    vec3 a = vec3(0.5, 0.5, 0.5);
    vec3 b = vec3(0.5, 0.5, 0.5);
    vec3 c = vec3(1.0, 1.0, 1.0);
    vec3 d = vec3(0.263, 0.416, 0.557);
    return a + b * cos(6.28318 * (c * t + d));
}

float Hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float StarLayer(vec2 uv) {
    vec2 gv = fract(uv) - 0.5;
    vec2 id = floor(uv);
    float m = 0.0;
    for(int y=-1; y<=1; y++) {
        for(int x=-1; x<=1; x++) {
            vec2 offset = vec2(x, y);
            float n = Hash21(id + offset);
            float size = fract(n * 345.32);
            float star = smoothstep(0.1, 0.0, length(gv - offset - vec2(n-0.5, fract(n*34.0)-0.5))) * size;
            m += star;
        }
    }
    return m;
}

void M(inout vec2 uv) { uv *= 1.0; }
void Q(inout vec2 uv) { uv *= 1.0; }

float sdBox(vec3 p, vec3 b) {
    vec3 q = abs(p) - b;
    return length(max(q,0.0)) + min(max(q.x,max(q.y,q.z)),0.0);
}

// New function that renders the scene for a specific ray (needed for RGB channel separation)
vec3 renderScene(vec2 screenUV, vec2 mouseM) {
    // === PARALLAX EFFECT (CAMERA OFFSET VIA MOUSE) ===
    // Camera position (ro) dynamically shifts with mouse input, creating a face parallax effect
    vec3 ro = vec3(mouseM * 0.8, -2.5);

    // Ray direction vector accounts for camera offset
    vec3 rd = normalize(vec3(screenUV - mouseM * 0.15, 1.5));

    float d3, t3 = 0.0;
    for(int i = 0; i < 80; i++) {
        vec3 p = ro + rd * t3;
        p.xz *= rot(iTime * 0.15);
        p.xy *= rot(iTime * 0.15);
        d3 = sdBox(p, vec3(1.0));
        if(d3 < 0.001 || t3 > 10.0) break;
        t3 += d3;
    }

    vec3 layerColor = vec3(0.0);

    if(d3 < 0.001) {
        vec3 p = ro + rd * t3;
        p.xz *= rot(iTime * 0.15);
        p.xy *= rot(iTime * 0.15);
        vec3 n = abs(p);

        vec2 faceUV = (n.x > n.y && n.x > n.z) ? p.yz : (n.y > n.z ? p.xz : p.xy);

        // === INNER TEXTURE PARALLAX ===
        // Project the ray (rd) into cube's local space to compute the view vector relative to the face
        vec3 rd_local = rd;
        rd_local.xz *= rot(iTime * 1.0);
        rd_local.xy *= rot(iTime * 1.0);

        // Offset UV coordinates based on the face viewing angle (rd_local components)
        // This simulates a "behind-the-glass" depth effect (Parallax Mapping)
        vec2 parallaxOffset = vec2(0.0);
        if (n.x > n.y && n.x > n.z) {
            parallaxOffset = rd_local.yz * 0.08;
        } else if (n.y > n.z) {
            parallaxOffset = rd_local.xz * 0.08;
        } else {
            parallaxOffset = rd_local.xy * 0.08;
        }

        vec2 uv = faceUV + parallaxOffset;
        vec2 uv3 = faceUV + parallaxOffset * 2.0; // Stronger parallax for stars (they sit deeper)

        uv *= rot(iTime * 0.02);
        uv3 *= rot(iTime * 0.02);

        float gridSize = 3.5;
        uv *= gridSize;
        uv3 *= gridSize;

        float t_shift = iTime * 0.9;

        // Chaotic XY shift
        float row1 = floor(uv.y);
        float xShift1 = (sin(t_shift + row1 * 0.8) + sin(t_shift * 0.5 - row1 * 1.3)) * 1.2;
        uv.x += xShift1; uv3.x += xShift1;

        float col1 = floor(uv.x);
        float yShift1 = (cos(t_shift * 0.7 + col1 * 0.6) + sin(t_shift * 1.2 + col1 * 1.1)) * 1.2;
        uv.y += yShift1; uv3.y += yShift1;

        // Chaotic YX shift
        float col2 = floor(uv.x);
        float yShift2 = (sin(t_shift * 1.1 - col2 * 0.9) * cos(t_shift * 0.4 + col2 * 0.5)) * 1.0;
        uv.y += yShift2; uv3.y += yShift2;

        float row2 = floor(uv.y);
        float xShift2 = (cos(t_shift * 0.6 + row2 * 1.4) * sin(t_shift * 1.3 - row2 * 0.7)) * 1.0;
        uv.x += xShift2; uv3.x += xShift2;

        vec2 tileID = floor(uv);
        uv = fract(uv) - 0.5;
        uv3 = fract(uv3) - 0.5;

        float t = iTime * 0.05;

        uv3.x = abs(uv3.x);
        uv3.y += tan((5.0 / 6.0) * PI) * 0.5;

        vec2 nVec = N((5.0 / 6.0) * PI);
        float d = dot(uv3 - vec2(0.5, 0.0), nVec);
        uv3 -= nVec * max(0.0, d) * 2.0;

        nVec = N((2.0 / 3.0) * PI);

        for(int i = 0; i < 4; i++) {
            uv3 *= 1.25;
            uv3.x -= 0.6;
            M(uv);
            uv3.x = abs(uv3.x);
            uv3.x -= 0.3;
            uv3 -= nVec * min(0.0, dot(uv3, nVec)) * 2.0;
        }

        uv3 += mouseM * 0.5;
        uv3 *= Rot(t);

        vec3 col4 = vec3(0.0);
        for(float i = 0.0; i < 1.0; i += 1.0 / NUM_LAYERS) {
            float depth = fract(i + t);
            float scale = mix(15.0, 0.5, depth);
            float fade = depth * smoothstep(1.0, 0.9, depth);
            col4 += StarLayer(uv3 * scale + i * 453.2) * fade;
        }

        M(uv);
        vec2 uv0 = uv;

        for (float i = 0.0; i < 4.0; i++) {
            uv = fract(uv * 1.5) - 0.5;
            Q(uv);
            float dPattern = length(uv) * exp(-length(uv0));

            vec3 col = palette(length(uv0) + i * 0.4 + iTime * 0.2 + Hash21(tileID) * 0.8);

            dPattern = sin(dPattern * 8.0 + iTime) / 8.0;
            dPattern = abs(dPattern);
            dPattern = pow(0.01 / dPattern, 1.2);

            layerColor += col * dPattern + col4 * 0.2;
        }

        vec2 gridLines = smoothstep(0.43, 0.49, abs(uv0));
        float border = 1.0 - max(gridLines.x, gridLines.y);
        vec3 edgeGlow = palette(iTime * 0.1) * max(gridLines.x, gridLines.y) * 0.4;

        layerColor = layerColor * border + edgeGlow;
        layerColor *= 1.0 - smoothstep(0.5, 1.5, length(uv0));

    } else {
        // Soft parallax for background stars
        layerColor = vec3(StarLayer(screenUV * 3.0 - mouseM * 0.2) * 0.4);
        layerColor += palette(iTime * 0.05) * 0.05 / (length(screenUV - mouseM * 0.1) - 0.4);
    }

    return layerColor;
}

// === MAIN LOOP ===
void mainImage( out vec4 fragColor, in vec2 fragCoord ) {
    vec2 screenUV = (fragCoord * 2.0 - iResolution.xy) / iResolution.y;
    vec2 mouseM = (iMouse.xy - iResolution.xy * 0.5) / iResolution.y;

    // If mouse is not pressed, smoothly animate the viewpoint using sine/cosine waves
    if (iMouse.z <= 0.0) {
        mouseM = vec2(sin(iTime * 0.05) * 0.3, cos(iTime * 0.03) * 0.2);
    }

    // ========================================================
    //  CHROMATIC ABERRATION CALCULATION
    // ========================================================
    vec2 shiftDir = normalize(screenUV) * length(screenUV);
    float aberrIntensity = 0.025 + sin(iTime * 2.0) * 0.008;

    vec2 uvRed   = screenUV - shiftDir * aberrIntensity;
    vec2 uvGreen = screenUV;
    vec2 uvBlue  = screenUV + shiftDir * aberrIntensity;

    float rChannel = renderScene(uvRed, mouseM).r;
    float gChannel = renderScene(uvGreen, mouseM).g;
    float bChannel = renderScene(uvBlue, mouseM).b;

    vec3 finalColor = vec3(rChannel, gChannel, bChannel);
    // ========================================================

    // Soft vignette effect along screen edges
    finalColor *= 1.0 - smoothstep(0.5, 1.8, length(screenUV));

    fragColor = vec4(finalColor, 1.0);
}
