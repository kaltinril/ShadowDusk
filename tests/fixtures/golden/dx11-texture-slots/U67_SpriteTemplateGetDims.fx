Texture2D SpriteTexture;
sampler2D SpriteTextureSampler = sampler_state { Texture = <SpriteTexture>; };
sampler2D MaskSampler;
float4 P(float4 pos : SV_POSITION, float4 color : COLOR0, float2 uv : TEXCOORD0) : SV_TARGET { float w, h; SpriteTexture.GetDimensions(w, h); float4 m = tex2D(MaskSampler, uv); return tex2D(SpriteTextureSampler, uv + float2(1 / w, 0)) * m * color; }
technique T { pass P0 { PixelShader = compile ps_4_0 P(); } }
