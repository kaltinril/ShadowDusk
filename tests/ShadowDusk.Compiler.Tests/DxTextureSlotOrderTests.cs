#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// Pure unit tests for <see cref="DxTextureSlotOrder"/>: the DirectX 11 <c>t</c> slot plan
/// (explicit registers reserved, a directly read modern resource kept on its register, then legacy
/// textures in evaluation order, then modern resources in declaration order, each on the lowest
/// free slot) and the declaration pinning that applies it. The plan against real <c>mgfxc</c>
/// goldens, on both DXBC backends, is <c>DirectX11TextureSlotOrderTests</c> (Integration).
/// </summary>
public sealed class DxTextureSlotOrderTests
{
    private static readonly IReadOnlyList<(string Name, int Slot)> NoRegisters = [];

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    [Fact]
    public void BloomCombine_TheSpriteSamplerReadFirstTakesT0()
    {
        // Declaration order put the undeclared state texture Tex2 (declared ahead of everything by
        // the rewrite) on t0; BloomSampler's texture is read first.
        var plan = DxTextureSlotOrder.Plan(
            [("Tex2", 0), ("BloomSampler_SDTexture", 1)],
            firstReadOrder: ["BloomSampler_SDTexture", "Tex2"],
            Set("Tex2", "BloomSampler_SDTexture"),
            NoRegisters);

        plan.ShouldNotBeNull();
        plan["BloomSampler_SDTexture"].ShouldBe(0);
        plan["Tex2"].ShouldBe(1);
    }

    [Fact]
    public void AlreadyInFirstReadOrder_IsNoPlan()
    {
        DxTextureSlotOrder.Plan([("A", 0), ("B", 1)], ["A", "B"], Set("A", "B"), NoRegisters).ShouldBeNull();
    }

    [Fact]
    public void NoLegacyTexture_IsNoPlan_EvenReadOutOfOrder()
    {
        // Modern-only shaders keep declaration order, which the backend already gives.
        DxTextureSlotOrder.Plan([("T1", 0), ("T2", 1)], ["T2", "T1"], Set("Other"), NoRegisters).ShouldBeNull();
    }

    [Fact]
    public void ModernResources_GoAfterEveryLegacyOne_InDeclarationOrder()
    {
        // `Texture2D T; SamplerState S; Texture2D U; sampler2D A;`, U read first, then A, then T.
        var plan = DxTextureSlotOrder.Plan([("T", 0), ("U", 1), ("A", 2)], ["U", "A", "T"], Set("A"), NoRegisters);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["A"] = 0, ["T"] = 1, ["U"] = 2 }, ignoreOrder: true);
    }

    [Fact]
    public void ExplicitRegisters_KeepAModernResource_AndReserveEvenUnread()
    {
        // `Texture2D T : register(t1)` read; `texture Unused : register(t0)` never read; legacy B
        // read before legacy A.
        var plan = DxTextureSlotOrder.Plan(
            [("A", 0), ("T", 1), ("B", 2)], ["B", "A", "T"], Set("A", "B"), [("T", 1), ("Unused", 0)]);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["B"] = 2, ["T"] = 1, ["A"] = 3 }, ignoreOrder: true);
    }

    [Fact]
    public void ARegisterOnALegacyStateTexture_OnlyReserves()
    {
        // `texture2D Diffuse : register(t1)` / `texture2D Spec : register(t0)`, named only by
        // sampler_state blocks: mgfxc puts the samplers' textures on t2/t3, in first-read order.
        var plan = DxTextureSlotOrder.Plan(
            [("Diffuse", 0), ("Spec", 1)], ["Diffuse", "Spec"], Set("Diffuse", "Spec"), [("Diffuse", 1), ("Spec", 0)]);

        plan.ShouldNotBeNull();
        plan.ShouldBe(new Dictionary<string, int> { ["Diffuse"] = 2, ["Spec"] = 3 }, ignoreOrder: true);
    }

    [Fact]
    public void ABufferOnAnExplicitRegister_IsKept_AndTheLegacyTextureGoesAround()
    {
        // `Buffer<float4> Buf : register(t0); sampler2D A;`
        DxTextureSlotOrder.Plan([("Buf", 0), ("A_SDTexture", 1)], ["Buf", "A_SDTexture"], Set("A_SDTexture"), [("Buf", 0)])
            .ShouldBeNull();
    }

    [Fact]
    public void Pin_AddsTheRegisterToEveryTopLevelDeclaration()
    {
        const string text = "#if OPENGL\nTexture2D A_SDTexture; SamplerState A;\n#else\nTexture2D A_SDTexture; SamplerState A;\n#endif\nTexture2D<float4>  B ;\n";
        string? pinned = DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["A_SDTexture"] = 1, ["B"] = 0 });

        pinned.ShouldBe("#if OPENGL\nTexture2D A_SDTexture : register(t1); SamplerState A;\n#else\nTexture2D A_SDTexture : register(t1); SamplerState A;\n#endif\nTexture2D<float4>  B : register(t0);\n");
    }

    [Fact]
    public void Pin_ReplacesAnExistingRegister_InEveryBranch()
    {
        // A dead `#if SM6` branch with its own registers is pinned too (harmless where it does not
        // compile); a stage-qualified register is replaced like a plain one.
        const string text = "#if SM6\nTexture2D TA : register(t0);\n#else\nTexture2D TA;\n#endif\nBuffer<float4> Buf : register(ps, t3);\n";
        DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["TA"] = 1, ["Buf"] = 0 })
            .ShouldBe("#if SM6\nTexture2D TA : register(t1);\n#else\nTexture2D TA : register(t1);\n#endif\nBuffer<float4> Buf : register(t0);\n");
    }

    [Fact]
    public void Pin_IgnoresComments_FunctionParameters_AndBodies()
    {
        const string text =
            "// old: Texture2D T : register(t3);\n/* Texture2D T, U; */\nTexture2D T;\n" +
            "float4 F(Texture2D T, SamplerState S, float2 uv) { Texture2D T2; return T.Sample(S, uv); }\n";
        DxTextureSlotOrder.Pin(text, new Dictionary<string, int> { ["T"] = 0 })
            .ShouldBe(
                "// old: Texture2D T : register(t3);\n/* Texture2D T, U; */\nTexture2D T : register(t0);\n" +
                "float4 F(Texture2D T, SamplerState S, float2 uv) { Texture2D T2; return T.Sample(S, uv); }\n");
    }

    [Theory]
    [InlineData("Texture2D T, U;\n")]                       // declarator list
    [InlineData("Texture2D T[2];\n")]                       // array
    [InlineData("DECLARE_TEXTURE(T, 0);\n")]                // declared through a macro: not found
    [InlineData("#define DECL Texture2D T;\nDECL\n")]       // only inside a directive
    [InlineData("Texture2D T < string a = \"x\"; >;\n")]    // annotation
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
    public void Matches_ComparesEveryResourceAgainstThePlan()
    {
        var plan = new Dictionary<string, int> { ["A"] = 0, ["B"] = 1 };
        DxTextureSlotOrder.Matches([("B", 1), ("A", 0)], plan).ShouldBeTrue();
        DxTextureSlotOrder.Matches([("A", 1), ("B", 0)], plan).ShouldBeFalse();
        DxTextureSlotOrder.Matches([("A", 0)], plan).ShouldBeFalse();
    }
}
