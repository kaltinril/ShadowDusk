// Exercises B4: an implicit vector truncation on ASSIGNMENT / initialization. GLSL silently narrows
// a wider vector into a narrower slot; stricter HLSL errors (-Werror,-Wconversion). Assigning a
// vec4 value into a vec2 (and a vec3 into a vec2) must emit an explicit truncating swizzle
// (.xy / .xyz) instead of the bare assignment.
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 a = iMouse;        // vec4 truncated into vec2 (initializer)
    vec3 c = iResolution;   // vec3 into vec3 (no truncation)
    vec2 b;
    b = c;                  // vec3 truncated into vec2 (plain assignment)
    fragColor = vec4(a + b, 0.0, 1.0);
}
