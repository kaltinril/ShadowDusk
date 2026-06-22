// Real-world ShaderToy raymarcher (CC0): "Mirror-like Happy Accident", https://www.shadertoy.com/view/3XdXWX
// Regression for several constructs that combine here in one in-subset shader:
//   - mat2(vec4) single-vector matrix constructor (the code-golf rotation form) -> float2x2(float4)
//   - compound-assign with a matrix: p.xy *= mat2(...)  (the v*M form -> mul(M, v), reversed operands)
//   - tanh() intrinsic (GLSL -> identical HLSL name)
//   - scientific-notation float literals: 1e-3, 2e4
// Unlike the earlier code-golf raymarcher this shader has NO undefined behavior: p is assigned before
// any read, and there is no unsequenced modify-and-read, so it compiles AND renders on every backend.
// Stored faithfully (shader body unedited); only this header comment was added.

float map(vec3 p) {
    // Domain repetition
    p = abs(fract(p) - 0.5);
    // Cylinder + planes SDF
    return abs(min(length(p.xy) - 0.175, min(p.x, p.y) + 1e-3)) + 1e-3;
}

vec3 estimateNormal(vec3 p) {
    float eps = 0.001;
    return normalize(vec3(
        map(p + vec3(eps, 0.0, 0.0)) - map(p - vec3(eps, 0.0, 0.0)),
        map(p + vec3(0.0, eps, 0.0)) - map(p - vec3(0.0, eps, 0.0)),
        map(p + vec3(0.0, 0.0, eps)) - map(p - vec3(0.0, 0.0, eps))
    ));
}

void mainImage(out vec4 O, in vec2 C) {
    vec2 r = iResolution.xy;
    vec2 uv = (C - 0.5 * r) / r.y;

    float t = iTime;
    float z = fract(dot(C, sin(C))) - 0.5;
    vec4 col = vec4(0.0);
    vec4 p;

    for (float i = 0.0; i < 77.0; i++) {
        // Ray direction
        p = vec4(z * normalize(vec3(C - 0.7 * r, r.y)), 0.1 * t);
        p.z += t;

        vec4 q = p;

        // Apply "bugged" rotation matrices for glitchy fractal distortion
        p.xy *= mat2(cos(2.0 + q.z + vec4(0,11,33,0)));
        p.xy *= mat2(cos(q + vec4(0,11,33,0)));

        // Distance estimation
        float d = map(p.xyz);

        // Estimate lighting
        vec3 pos = p.xyz;
        vec3 lightDir = normalize(vec3(0.3, 0.5, 1.0));
        vec3 viewDir = normalize(vec3(uv, 1.0));
        vec3 n = estimateNormal(pos);
        vec3 reflectDir = reflect(viewDir, n);

        // Fake environment reflection (sky blue + fade to white)
        vec3 envColor = mix(vec3(0.8, 0.4, 0.8), vec3(1.0), 0.5 + 0.5 * reflectDir.y);

        // Specular highlight
        float spec = pow(max(dot(reflectDir, lightDir), 0.0), 32.0);

        // Funky palette color using original method
        vec4 baseColor = (1.0 + sin(0.5 * q.z + length(p.xyz - q.xyz) + vec4(0,4,3,6)))
                       / (0.5 + 2.0 * dot(q.xy, q.xy));

        // Combine base color + environment reflection + specular highlight
        vec3 finalColor = baseColor.rgb * 0.1 + envColor * 0.9 + vec3(spec) * 1.2;

        // Brightness weighted accumulation
        col.rgb += finalColor / d;

        z += 0.6 * d;
    }

    // Compress brightness range
    O = vec4(tanh(col.rgb / 2e4), 1.0);
}
