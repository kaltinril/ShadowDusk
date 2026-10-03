#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL.D3DCompiler;
using ShadowDusk.HLSL.Reflection;
using ShadowDusk.HLSL.Tests.Vkd3d;
using ShadowDusk.HLSL.Vkd3d;
using Shouldly;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Reflection;

/// <summary>
/// Issue #339, on real vkd3d-shader DXBC (Shader Model 5, ShadowDusk's DirectX 11 compile model):
/// the RDEF stores an ARRAY of textures as one record per element (<c>Tex[0]</c> t0, <c>Tex[1]</c>
/// t1), which <see cref="RdefReader"/> reads faithfully (its D3DReflect parity contract), and which
/// <see cref="DxbcReflectionExtractor"/> folds into the one binding mgfxc's DirectX_11 table is
/// built from (<c>Tex</c>, slot 0, two elements). Before the fix the extractor returned the raw
/// records and the effect carried parameters <c>Tex[0]</c> and <c>Tex[1]</c>.
/// </summary>
public sealed class DxbcResourceArrayReflectionTests
{
    private const string TextureArray = """
        Texture2D Tex[2] : register(t0);
        SamplerState TexSampler : register(s0);
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
        {
            return Tex[0].Sample(TexSampler, uv) + Tex[1].Sample(TexSampler, uv);
        }
        """;

    private const string SamplerArray = """
        Texture2D TexA : register(t0);
        Texture2D TexB : register(t1);
        SamplerState Samplers[2] : register(s0);
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
        {
            return TexA.Sample(Samplers[0], uv) + TexB.Sample(Samplers[1], uv);
        }
        """;

    private const string ArrayAfterATexture = """
        Texture2D Other : register(t0);
        Texture2D Tex[2] : register(t1);
        SamplerState TexSampler : register(s0);
        float4 MainPS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target0
        {
            return Other.Sample(TexSampler, uv) + Tex[0].Sample(TexSampler, uv) + Tex[1].Sample(TexSampler, uv);
        }
        """;

    [Vkd3dFact]
    public void Extractor_FoldsThePerElementRecords_IntoOneTextureAtTheBaseSlot()
    {
        ReadOnlyMemory<byte> dxbc = Compile(TextureArray);

        // The RDEF as stored (SM5): one record per element. This is what D3DReflect reports too.
        var raw = RdefReader.Read(dxbc.Span);
        raw.IsSuccess.ShouldBeTrue(raw.IsFailure ? raw.Error.Message : "ok");
        raw.Value.Textures.Select(t => (t.Name, t.BindSlot)).ShouldBe([("Tex[0]", 0), ("Tex[1]", 1)]);

        // The extractor's view: mgfxc's (fxc at the author's ps_4_0 reflects `Tex` t0 count 2).
        var extracted = new DxbcReflectionExtractor().Extract(dxbc);
        extracted.IsSuccess.ShouldBeTrue(extracted.IsFailure ? extracted.Error.Message : "ok");
        TextureReflection tex = extracted.Value.Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.BindSlot.ShouldBe(0);
        tex.ArrayLength.ShouldBe(2);
        tex.Dimension.ShouldBe(TextureDimension.Texture2D);
        extracted.Value.Samplers.ShouldHaveSingleItem().Name.ShouldBe("TexSampler");
    }

    [Vkd3dFact]
    public void Extractor_FoldsASamplerArray_TheSameWay()
    {
        ReadOnlyMemory<byte> dxbc = Compile(SamplerArray);

        var raw = RdefReader.Read(dxbc.Span);
        raw.IsSuccess.ShouldBeTrue(raw.IsFailure ? raw.Error.Message : "ok");
        raw.Value.Samplers.Select(s => (s.Name, s.BindSlot)).ShouldBe([("Samplers[0]", 0), ("Samplers[1]", 1)]);

        var extracted = new DxbcReflectionExtractor().Extract(dxbc);
        extracted.IsSuccess.ShouldBeTrue(extracted.IsFailure ? extracted.Error.Message : "ok");
        SamplerReflection samp = extracted.Value.Samplers.ShouldHaveSingleItem();
        samp.Name.ShouldBe("Samplers");
        samp.BindSlot.ShouldBe(0);
        samp.ArrayLength.ShouldBe(2);
        extracted.Value.Textures.Select(t => t.Name).ShouldBe(["TexA", "TexB"]);
    }

    [Vkd3dFact]
    public void Extractor_KeepsRdefOrder_AndTheArraysOwnBaseSlot()
    {
        ReadOnlyMemory<byte> dxbc = Compile(ArrayAfterATexture);

        var extracted = new DxbcReflectionExtractor().Extract(dxbc);
        extracted.IsSuccess.ShouldBeTrue(extracted.IsFailure ? extracted.Error.Message : "ok");
        extracted.Value.Textures.Select(t => (t.Name, t.BindSlot, t.ArrayLength))
            .ShouldBe([("Other", 0, (int?)null), ("Tex", 1, 2)]);
    }

    [Vkd3dFact(requiresD3DReflect: true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RdefReader_StillEqualsD3DReflect_OnAnArrayBlob()
    {
        // The parity contract the collapse must NOT disturb: the reader is the RDEF as D3DReflect
        // sees it, per-element records included. The collapse lives one layer up.
        ReadOnlyMemory<byte> dxbc = Compile(TextureArray);

        var managed = RdefReader.Read(dxbc.Span);
        var oracle  = D3DReflectOracle.Extract(dxbc);
        managed.IsSuccess.ShouldBeTrue();
        oracle.IsSuccess.ShouldBeTrue(oracle.IsFailure ? oracle.Error.Message : "ok");
        managed.Value.Textures.Select(t => (t.Name, t.BindSlot, t.ArrayLength))
            .ShouldBe(oracle.Value.Textures.Select(t => (t.Name, t.BindSlot, t.ArrayLength)));
        oracle.Value.Textures.Select(t => t.Name).ShouldBe(["Tex[0]", "Tex[1]"]);
    }

    private static ReadOnlyMemory<byte> Compile(string hlsl)
    {
        var result = new Vkd3dShaderCompiler().Compile(new D3DCompileRequest
        {
            HlslSource     = hlsl,
            SourceFileName = "ResourceArray.hlsl",
            EntryPoint     = "MainPS",
            Stage          = ShaderStage.Pixel,
        });
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : "vkd3d compile must succeed");
        return result.Value.Bytes;
    }
}
