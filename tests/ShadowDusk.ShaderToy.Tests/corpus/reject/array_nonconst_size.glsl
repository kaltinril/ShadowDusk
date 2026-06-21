// A non-constant-integer array size is a loud, located reject (the size must be a constant integer
// literal; a variable/expression size is outside the supported subset).
void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    int n = int(fragCoord.x);
    float a[n];
    fragColor = vec4(a[0], 0.0, 0.0, 1.0);
}
