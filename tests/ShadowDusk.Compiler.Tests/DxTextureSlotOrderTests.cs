#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for <see cref="DxTextureSlotOrder"/>: the DirectX 11 texture slot plan
/// (explicit registers fixed, then legacy textures in first-read order, then modern textures in
/// declaration order, each on the lowest free slot) and the declaration pinning that applies it.
/// The plan against real <c>mgfxc</c> goldens is <c>DirectX11TextureSlotOrderTests</c> (Integration).
/// </summary>
public sealed class DxTextureSlotOrderTests
{
    private static TextureReflection Tex(string name, int slot) =>
        new() { Name = name, BindSlot = slot, Dimension = TextureDimension.Texture2D };

    private static readonly IReadOnlyDictionary<string, int> NoRegisters = new Dictionary<string, int>(StringComparer.Ordinal);

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    [Fact]
    public void BloomCombine_TheSpriteSamplerReadFirstTakesT0()
    {
        // Declaration order put the undeclared state texture Tex2 (declared ahead of everything by
        // the rewrite) on t0; BloomSampler's texture is read first.
        var plan = DxTextureSlotOrder.Plan(
            [Tex("Tex2", 0), Tex("BloomSampler_SDTexture", 1)],
            firstReadOrder: [1, 0],
            Set("Tex2", "BloomSampler_SDTexture"),
            NoRegisters);

        plan.ShouldNotBeNull();
        plan["BloomSampler_SDTexture"].ShouldBe(0);
        plan["Tex2"].ShouldBe(1);
    }

    [Fact]
    public void AlreadyInFirstReadOrder_IsNoPlan()
    {
        DxTextureSlotOrder.Plan([Tex("A", 0), Tex("B", 1)], [0, 1], Set("A", "B"), NoRegisters).ShouldBeNull();
    }

    [Fact]
    public void NoLegacyTexture_IsNoPlan_EvenReadOutOfOrder()
    {
        // Modern-only shaders keep declaration order, which the backend already gives.
        DxTextureSlotOrder.Plan([Tex("T1", 0), Tex("T2", 1)], [1, 0], Set("Other"), NoRegisters).ShouldBeNull();
    }

    [Fact]
    public void ModernTextures_GoAfterEveryLegacyOne_InDeclarationOrder()
    {
        // `Texture2D T; SamplerState S; Texture2D U; sampler2D A;`, U read first, then A, then T.
        var plan = DxTextureSlotOrder.Plan(
            [Tex("T", 0), Tex("U", 1), Tex("A", 2)], [1, 2, 0], Set("A"), NoRegisters);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["A"] = 0, ["T"] = 1, ["U"] = 2 }, ignoreOrder: true);
    }

    [Fact]
    public void ExplicitRegisters_AreFixed_AndReserved_EvenUnread()
    {
        // `Texture2D T : register(t1)` read; `texture Unused : register(t0)` never read; legacy B
        // read before legacy A.
        var registers = new Dictionary<string, int>(StringComparer.Ordinal) { ["T"] = 1, ["Unused"] = 0 };
        var plan = DxTextureSlotOrder.Plan(
            [Tex("A", 0), Tex("T", 1), Tex("B", 2)], [2, 0, 1], Set("A", "B"), registers);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["B"] = 2, ["T"] = 1, ["A"] = 3 }, ignoreOrder: true);
    }

    [Fact]
    public void AnExplicitRegisterOnALegacyStateTexture_IsHonoured()
    {
        // The rewrite dropped `texture2D Diffuse : register(t1)`, so the backend put it on t0.
        var registers = new Dictionary<string, int>(StringComparer.Ordinal) { ["Diffuse"] = 1, ["Spec"] = 0 };
        var plan = DxTextureSlotOrder.Plan(
            [Tex("Diffuse", 0), Tex("Spec", 1)], [0, 1], Set("Diffuse", "Spec"), registers);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["Diffuse"] = 1, ["Spec"] = 0 }, ignoreOrder: true);
    }

    [Fact]
    public void ATextureArray_IsNotPlanned()
    {
        var array = new TextureReflection { Name = "Arr", BindSlot = 0, Dimension = TextureDimension.Texture2D, ArrayLength = 2 };
        DxTextureSlotOrder.Plan([array, Tex("A", 2)], [2, 0], Set("A"), NoRegisters).ShouldBeNull();
    }

    [Fact]
    public void Pin_AddsTheRegisterToEveryPlainDeclaration()
    {
        const string text = "#if OPENGL\nTexture2D A_SDTexture; SamplerState A;\n#else\nTexture2D A_SDTexture; SamplerState A;\n#endif\nTexture2D<float4>  B ;\n";
        string? pinned = DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["A_SDTexture"] = 1, ["B"] = 0 });

        pinned.ShouldBe("#if OPENGL\nTexture2D A_SDTexture : register(t1); SamplerState A;\n#else\nTexture2D A_SDTexture : register(t1); SamplerState A;\n#endif\nTexture2D<float4>  B : register(t0);\n");
    }

    [Fact]
    public void Pin_LeavesADeclarationAlreadyOnItsRegister()
    {
        const string text = "Texture2D T : register(t2);\nTexture2D A_SDTexture;\n";
        DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["T"] = 2, ["A_SDTexture"] = 0 })
            .ShouldBe("Texture2D T : register(t2);\nTexture2D A_SDTexture : register(t0);\n");
    }

    [Theory]
    [InlineData("Texture2D T : register(t3);\n")]          // already on another register
    [InlineData("Texture2D T, U;\n")]                      // declarator list
    [InlineData("DECLARE_TEXTURE(T, 0);\n")]               // declared through a macro: not found
    [InlineData("Texture2D T;\nTexture2D T : register(t1);\n")] // one plain, one on another register
    public void Pin_RefusesADeclarationItCannotPinSafely(string text)
    {
        DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["T"] = 0 }).ShouldBeNull();
    }

    [Fact]
    public void Pin_DoesNotTouchALongerNameOrAUse()
    {
        const string text = "Texture2D Tex; Texture2D TexB;\nfloat4 F(float2 uv) { return Tex.Sample(S, uv) + TexB.Sample(S, uv); }\n";
        DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["Tex"] = 1 })
            .ShouldBe("Texture2D Tex : register(t1); Texture2D TexB;\nfloat4 F(float2 uv) { return Tex.Sample(S, uv) + TexB.Sample(S, uv); }\n");
    }

    [Fact]
    public void Matches_ComparesEveryTextureAgainstThePlan()
    {
        var plan = new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 };
        DxTextureSlotOrder.Matches([Tex("B", 1), Tex("A", 0)], plan).ShouldBeTrue();
        DxTextureSlotOrder.Matches([Tex("A", 1), Tex("B", 0)], plan).ShouldBeFalse();
        DxTextureSlotOrder.Matches([Tex("A", 0)], plan).ShouldBeFalse();
    }
}
