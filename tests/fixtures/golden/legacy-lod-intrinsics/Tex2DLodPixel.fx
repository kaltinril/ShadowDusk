// tex2Dlod / tex2Dbias / tex2Dproj in a PIXEL shader only, legacy D3D9 sampler syntax, at the
// SM3 profiles the OpenGL target compiles (the vertex-stage spelling is refused there with
// SD0210, as real mgfxc refuses it: MojoShader-era GL runtimes bind no vertex textures).
// SpriteBatch-compatible: the sprite texture is the bare sampler's (s0).
//
// Reference: real mgfxc 3.8.4.1 /Profile:OpenGL and /Profile:DirectX_11 builds beside it.
// Measured 2026-10-28: mgfxc's OpenGL GLSL samples tex2Dlod with a plain texture2D (MojoShader
// drops the explicit LOD in a pixel shader), keeps the bias (texture2D(s, uv, bias)) and the
// divide (texture2DProj). ShadowDusk's OpenGL output honors the LOD (texture2DLod behind
// MojoShader's guarded GL_ARB_shader_texture_lod header), exactly as it already does for
// Texture2D.SampleLevel; see project_facts.md.

#if OPENGL
#define PS_SHADERMODEL ps_3_0
#else
#define PS_SHADERMODEL ps_4_0
#endif

sampler2D SpriteSampler : register(s0);

float4 MainPS(float4 position : SV_Position, float4 color : COLOR0, float2 uv : TEXCOORD0) : COLOR0
{
    float4 lod  = tex2Dlod(SpriteSampler, float4(uv, 0, 1));
    float4 bias = tex2Dbias(SpriteSampler, float4(uv, 0, 1));
    float4 proj = tex2Dproj(SpriteSampler, float4(uv * 2, 0, 2));
    return (lod + bias + proj) / 3 * color;
}

technique LegacyLodPixel
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
}
