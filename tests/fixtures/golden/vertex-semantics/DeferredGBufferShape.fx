// The shape of KNI's EffectTest `DeferredBasicEffect` (a deferred-renderer G-buffer pass),
// written independently: POSITION / NORMAL / TEXCOORD / BINORMAL / TANGENT vertex inputs, a
// float3x3 tangent-to-world interpolant at TEXCOORD2, and three render-target outputs. On
// OpenGL it used to fail with SD0210 at BINORMAL0, then at the float3x3 interpolant.
float4x4 World;
float4x4 View;
float4x4 Projection;

texture2D AlbedoMap : register(t0);
sampler AlbedoSampler : register(s0) = sampler_state
{
    Texture = (AlbedoMap);
    MagFilter = LINEAR;
    MinFilter = LINEAR;
    MipFilter = LINEAR;
    AddressU = Wrap;
    AddressV = Wrap;
};

texture2D GlossMap : register(t1);
sampler GlossSampler : register(s1) = sampler_state
{
    Texture = (GlossMap);
    MagFilter = LINEAR;
    MinFilter = LINEAR;
    MipFilter = LINEAR;
    AddressU = Wrap;
    AddressV = Wrap;
};

struct GBufferVertex
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 TexCoord : TEXCOORD0;
    float3 Binormal : BINORMAL0;
    float3 Tangent  : TANGENT0;
};

struct GBufferInterpolants
{
    float4 Position     : POSITION0;
    float2 TexCoord     : TEXCOORD0;
    float2 ClipDepth    : TEXCOORD1;
    float3x3 TangentToWorld : TEXCOORD2;
};

struct GBufferTargets
{
    float4 Albedo : COLOR0;
    float4 Normal : COLOR1;
    float4 Depth  : COLOR2;
};

GBufferInterpolants GBufferVS(GBufferVertex input)
{
    GBufferInterpolants output;
    float4 worldPosition = mul(float4(input.Position.xyz, 1), World);
    output.Position = mul(mul(worldPosition, View), Projection);
    output.TexCoord = input.TexCoord;
    output.ClipDepth = output.Position.zw;
    float3x3 world3 = (float3x3)World;
    output.TangentToWorld[0] = mul(input.Tangent, world3);
    output.TangentToWorld[1] = mul(input.Binormal, world3);
    output.TangentToWorld[2] = mul(input.Normal, world3);
    return output;
}

GBufferTargets GBufferPS(GBufferInterpolants input)
{
    GBufferTargets output;
    output.Albedo = tex2D(AlbedoSampler, input.TexCoord);
    float4 gloss = tex2D(GlossSampler, input.TexCoord);
    output.Albedo.a = gloss.r;
    float3 normal = normalize(mul(float3(0, 0, 1), input.TangentToWorld));
    output.Normal = float4(0.5 * (normal + 1), gloss.a);
    output.Depth = input.ClipDepth.x / input.ClipDepth.y;
    return output;
}

technique GBuffer
{
    pass { VertexShader = compile vs_3_0 GBufferVS(); PixelShader = compile ps_3_0 GBufferPS(); }
}
