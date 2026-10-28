// OpenGL only: DXC accepts a function call as a global initializer (and drops the value), vkd3d
// refuses it. ShadowDusk does not evaluate user function calls, so it warns (SD0017) and ships zero.
#define PS_SHADERMODEL ps_3_0

float Twice(float x) { return x * 2; }

float Good = 0.5;
float Unmodelled = Twice(0.25);

float4 MainPS() : COLOR0
{
    return float4(Good, Unmodelled, 0, 1);
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};
