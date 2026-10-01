// Adversarial (issue #228): the SAME generic struct and the SAME generic function
// instantiated repeatedly with different type arguments inside one entry point, each
// needing a genuinely different compiled body under one surface name.
struct Box<T : IArithmetic>
{
    T value;
    T twice() { return value + value; }
}

struct Pair<T, U>
{
    T a;
    U b;
}

T twice<T : IArithmetic>(T x) { return x + x; }
T square<T : IArithmetic>(T x) { return x * x; }

Texture2D SpriteTexture : register(t0);
SamplerState SpriteTextureSampler : register(s0);

cbuffer Params : register(b0)
{
    float Gain;
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
    float4 tex = SpriteTexture.Sample(SpriteTextureSampler, i.UV);
    Box<float> bf; bf.value = i.UV.x;
    Box<float3> b3; b3.value = float3(i.UV, Gain);
    Box<float4> b4; b4.value = Tint;
    Pair<float, float2> p; p.a = twice<float>(i.UV.x); p.b = twice<float2>(i.UV);
    Pair<float2, float> q; q.a = i.UV; q.b = square<float>(Gain);
    float r = bf.twice() + b3.twice().x + square(i.UV).x;
    float g = b4.twice().y + p.b.y + q.a.x;
    return float4(r, g, p.a + q.b, 1) * tex;
}
