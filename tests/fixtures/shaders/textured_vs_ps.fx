Texture2D Texture0;
SamplerState Sampler0;

struct VSInput { float4 Position : POSITION; float2 UV : TEXCOORD0; };
struct VSOutput { float4 Position : SV_Position; float2 UV : TEXCOORD0; };

VSOutput VSMain(VSInput input) {
    VSOutput output;
    output.Position = input.Position;
    output.UV = input.UV;
    return output;
}

float4 PSMain(VSOutput input) : SV_Target {
    return Texture0.Sample(Sampler0, input.UV);
}
