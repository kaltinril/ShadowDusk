// Adversarial (issue #228): author-declared names shaped like slangc's own mangled ones
// ('Box_0', 'helper_0', 'value_0'), declared alongside generics that slangc will itself
// instantiate as 'Box_N' / 'helper_N'.
struct Box_0 { float value_0; }

struct Box<T : IArithmetic>
{
    T value;
    T twice() { return value + value; }
}

float helper_0(float x) { return x + 1.0; }
T helper<T : IArithmetic>(T x) { return x * x; }

cbuffer Params : register(b0)
{
    float Gain;
    float Gain_0;
    float4 Tint;
};

struct PsInput
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD0;
};

[shader("fragment")]
float4 MainPS(PsInput i) : SV_Target0
{
    Box_0 user; user.value_0 = helper_0(i.UV.x);
    Box<float> g1; g1.value = helper(Gain);
    Box<float2> g2; g2.value = helper(i.UV);
    return float4(user.value_0 + g1.twice(), g2.twice().y + Gain_0, helper<float4>(Tint).x, 1);
}
