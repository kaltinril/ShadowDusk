// Adversarial (issue #228): one generic function over an interface, instantiated with two
// conforming types whose bodies differ, in the vertex AND the pixel entry, in opposite
// order. slangc numbers instantiations per translation unit (one per entry), so the same
// '_N' name can mean different bodies in the two units.
interface IShape
{
    float4 tint(float4 c);
}

struct Warm : IShape { float4 tint(float4 c) { return c * float4(1.0, 0.8, 0.6, 1.0); } }
struct Cool : IShape { float4 tint(float4 c) { return c * float4(0.6, 0.8, 1.0, 1.0); } }

float4 apply<T : IShape>(T shape, float4 c) { return shape.tint(c); }

cbuffer Params : register(b0)
{
    float4x4 WorldViewProjection;
    float Mix;
};

struct VsIn
{
    float4 Position : POSITION0;
    float4 Color : COLOR0;
};

struct VsOut
{
    float4 Position : SV_Position;
    float4 Color : COLOR0;
};

[shader("vertex")]
VsOut MainVS(VsIn v)
{
    VsOut o;
    Warm w; Cool c;
    o.Position = mul(v.Position, WorldViewProjection);
    o.Color = lerp(apply(w, v.Color), apply(c, v.Color), Mix);
    return o;
}

[shader("fragment")]
float4 MainPS(VsOut i) : SV_Target0
{
    Warm w; Cool c;
    return lerp(apply(c, i.Color), apply(w, i.Color), Mix);
}
