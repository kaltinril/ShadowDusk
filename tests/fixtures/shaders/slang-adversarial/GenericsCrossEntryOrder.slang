struct Box<T : IArithmetic> { T value; T twice() { return value + value; } }
T helper<T : IArithmetic>(T x) { return x * x; }

cbuffer Params : register(b0) { float4x4 WVP; float Gain; };

struct VsIn { float4 Position : POSITION0; float2 UV : TEXCOORD0; };
struct VsOut { float4 Position : SV_Position; float2 UV : TEXCOORD0; };

[shader("vertex")]
VsOut MainVS(VsIn v)
{
    VsOut o;
    Box<float2> b; b.value = v.UV;
    float2 a = helper<float2>(v.UV);
    float c = helper<float>(Gain);
    o.Position = mul(v.Position, WVP);
    o.UV = a + b.twice() * c;
    return o;
}

[shader("fragment")]
float4 MainPS(VsOut i) : SV_Target0
{
    Box<float> b; b.value = i.UV.x;
    float a = helper<float>(i.UV.x);
    float2 c = helper<float2>(i.UV);
    return float4(a + b.twice(), c, 1);
}
