texture2D Diffuse : register(t0);
sampler DiffuseSampler : register(s0) = sampler_state { Texture = (Diffuse); };
texture2D Specular : register(t1);
sampler SpecularSampler : register(s1) = sampler_state { Texture = (Specular); };
texture2D Normal : register(t2);
sampler NormalSampler : register(s2) = sampler_state { Texture = (Normal); };
float4 P(float2 uv : TEXCOORD0) : COLOR0
{
    float4 c = tex2D(DiffuseSampler, uv);
    c.a = tex2D(SpecularSampler, uv).r;
    float3 n = tex2D(NormalSampler, uv).rgb;
    n = float3(0, 0, 1);
    return c * n.z;
}
technique T { pass P0 { PixelShader = compile ps_4_0_level_9_1 P(); } }
