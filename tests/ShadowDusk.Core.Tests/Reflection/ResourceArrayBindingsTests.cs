#nullable enable

using ShadowDusk.Core.Reflection;
using Shouldly;
using Xunit;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Issue #339: <see cref="ResourceArrayBindings.Collapse"/> folds the per-element bindings a Shader
/// Model 5 RDEF stores for an array (<c>Tex[0]</c> t0, <c>Tex[1]</c> t1, ...) into the one binding
/// Shader Model 4 reflection reports and mgfxc's DirectX_11 table is built from (<c>Tex</c> at the
/// array's base slot). The shapes are the ones measured against d3dcompiler_47 at <c>ps_4_0</c> on
/// 2026-10-02 (see the class remarks of <see cref="ResourceArrayBindings"/>). Pure: hand-built
/// reflections, no compiler.
/// </summary>
public sealed class ResourceArrayBindingsTests
{
    private static TextureReflection Texture(string name, int slot) => new()
    {
        Name = name, BindSlot = slot, Dimension = TextureDimension.Texture2D,
    };

    private static SamplerReflection Sampler(string name, int slot) => new() { Name = name, BindSlot = slot };

    private static ReflectedEffect Effect(IReadOnlyList<TextureReflection> textures, IReadOnlyList<SamplerReflection> samplers) => new()
    {
        ConstantBuffers = [],
        Textures        = textures,
        Samplers        = samplers,
        InputSignature  = [],
        OutputSignature = [],
        Parameters      = [],
    };

    [Fact]
    public void ArrayFromSlotZero_BecomesOneBinding_AtSlotZero_WithTheElementCount()
    {
        // Texture2D Tex[2] : register(t0), SM5 RDEF: Tex[0] t0, Tex[1] t1. mgfxc (SM4): Tex t0 count 2.
        var effect = Effect([Texture("Tex[0]", 0), Texture("Tex[1]", 1)], [Sampler("TexSampler", 0)]);

        ReflectedEffect collapsed = ResourceArrayBindings.Collapse(effect);

        TextureReflection tex = collapsed.Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.BindSlot.ShouldBe(0);
        tex.ArrayLength.ShouldBe(2);
        tex.Dimension.ShouldBe(TextureDimension.Texture2D);
        collapsed.Samplers.ShouldHaveSingleItem().Name.ShouldBe("TexSampler");
    }

    [Fact]
    public void OnlyElementOneRead_StillReportsTheArrayBase_AtSlotZero()
    {
        // `Tex[1].Sample(...)` alone: SM5 RDEF carries only Tex[1] at t1; fxc at SM4 still binds the
        // whole array (Tex t0 count 2) and mgfxc writes its one record at slot 0.
        var effect = Effect([Texture("Tex[1]", 1)], [Sampler("TexSampler", 0)]);

        TextureReflection tex = ResourceArrayBindings.Collapse(effect).Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.BindSlot.ShouldBe(0, "the base register is slot - index, not the first slot in use");
        tex.ArrayLength.ShouldBe(2, "the highest index read plus one");
    }

    [Fact]
    public void SparseElements_CollapseToTheBase_WithTheHighestIndexPlusOne()
    {
        // Tex[0] + Tex[3] of Texture2D Tex[4]: SM5 Tex[0] t0, Tex[3] t3; SM4 Tex t0 count 4.
        var effect = Effect([Texture("Tex[0]", 0), Texture("Tex[3]", 3)], [Sampler("TexSampler", 0)]);

        TextureReflection tex = ResourceArrayBindings.Collapse(effect).Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.BindSlot.ShouldBe(0);
        tex.ArrayLength.ShouldBe(4);
    }

    [Fact]
    public void ArrayDeclaredAfterATexture_KeepsRdefOrder_AndItsOwnBase()
    {
        // Texture2D Other : register(t0); Texture2D Tex[2] : register(t1). SM4: Other t0, Tex t1 count 2;
        // mgfxc writes record 0 for Other at slot 0 and record 1 for Tex at slot 1, parameters in that order.
        var effect = Effect([Texture("Other", 0), Texture("Tex[0]", 1), Texture("Tex[1]", 2)], [Sampler("TexSampler", 0)]);

        var textures = ResourceArrayBindings.Collapse(effect).Textures;
        textures.Count.ShouldBe(2);
        textures[0].Name.ShouldBe("Other");
        textures[0].BindSlot.ShouldBe(0);
        textures[0].ArrayLength.ShouldBeNull();
        textures[1].Name.ShouldBe("Tex");
        textures[1].BindSlot.ShouldBe(1);
        textures[1].ArrayLength.ShouldBe(2);
    }

    [Fact]
    public void ElementsOutOfOrder_StillFormOneBinding_InFirstOccurrencePosition()
    {
        var effect = Effect([Texture("Tex[1]", 1), Texture("Other", 2), Texture("Tex[0]", 0)], []);

        var textures = ResourceArrayBindings.Collapse(effect).Textures;
        textures.Count.ShouldBe(2);
        textures[0].Name.ShouldBe("Tex");
        textures[0].BindSlot.ShouldBe(0);
        textures[0].ArrayLength.ShouldBe(2);
        textures[1].Name.ShouldBe("Other");
    }

    [Fact]
    public void SamplerArray_CollapsesTheSameWay()
    {
        // SamplerState TexSampler[2] : register(s0): SM5 TexSampler[0] s0, TexSampler[1] s1; SM4 TexSampler s0 count 2.
        var effect = Effect([Texture("Tex[0]", 0), Texture("Tex[1]", 1)], [Sampler("TexSampler[0]", 0), Sampler("TexSampler[1]", 1)]);

        ReflectedEffect collapsed = ResourceArrayBindings.Collapse(effect);
        SamplerReflection samp = collapsed.Samplers.ShouldHaveSingleItem();
        samp.Name.ShouldBe("TexSampler");
        samp.BindSlot.ShouldBe(0);
        samp.ArrayLength.ShouldBe(2);
        collapsed.Textures.ShouldHaveSingleItem().ArrayLength.ShouldBe(2);
    }

    [Fact]
    public void SingleElementArray_IsReportedAsAnArrayOfOne()
    {
        // Texture2D Tex[1]: SM5 stores Tex[0] t0. mgfxc's parameter is `Tex`; a 1-element array
        // is distinguishable here (unlike DXIL's BindCount) and the name must still lose its index.
        var effect = Effect([Texture("Tex[0]", 0)], []);

        TextureReflection tex = ResourceArrayBindings.Collapse(effect).Textures.ShouldHaveSingleItem();
        tex.Name.ShouldBe("Tex");
        tex.ArrayLength.ShouldBe(1);
    }

    [Fact]
    public void NoArray_ReturnsTheSameInstance_Untouched()
    {
        // The byte-identity guarantee for every array-free effect: nothing is rebuilt.
        var effect = Effect([Texture("Diffuse", 0), Texture("Lightmap", 1)], [Sampler("DiffuseSampler", 0), Sampler("LightmapSampler", 1)]);

        ResourceArrayBindings.Collapse(effect).ShouldBeSameAs(effect);
    }

    [Theory]
    [InlineData("Tex[0]", true)]
    [InlineData("Tex[12]", true)]
    [InlineData("Tex", false)]
    [InlineData("Tex_0", false)]
    [InlineData("Tex[]", false)]
    [InlineData("Tex[a]", false)]
    public void IsElement_MatchesOnlyTheIndexedSpelling(string name, bool expected)
    {
        ResourceArrayBindings.IsElement(name).ShouldBe(expected);
    }
}
