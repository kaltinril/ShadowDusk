#nullable enable

using Shouldly;
using ShadowDusk.Compiler.Internal;
using ShadowDusk.Core.Reflection;
using Xunit;

namespace ShadowDusk.Compiler.Tests;

/// <summary>
/// <see cref="VulkanResourceSlots"/>: a Vulkan texture's or sampler's slot is its SPIR-V binding
/// minus 32 (mgfxc 3.8.5's <c>BindingSlot - SlotOffset</c>, and the <c>binding - 32</c> MonoGame's
/// DesktopVK descriptor writer reads back), never its ordinal among the stage's resources. The two
/// differ exactly when the explicit registers do not start at 0 or have a gap.
/// </summary>
public sealed class VulkanResourceSlotsTests
{
    private static ReflectedEffect Effect(
        IEnumerable<(string Name, int Binding)> textures,
        IEnumerable<(string Name, int Binding)> samplers) => new()
    {
        ConstantBuffers = [],
        // BindSlot as the SPIR-V reflector writes it: the ordinal in binding order.
        Textures = textures.OrderBy(t => t.Binding).Select((t, i) => new TextureReflection
        {
            Name = t.Name, BindSlot = i, Dimension = TextureDimension.Texture2D, RawBinding = t.Binding, RawDescriptorSet = 1,
        }).ToList(),
        Samplers = samplers.OrderBy(s => s.Binding).Select((s, i) => new SamplerReflection
        {
            Name = s.Name, BindSlot = i, RawBinding = s.Binding, RawDescriptorSet = 1,
        }).ToList(),
        InputSignature = [],
        OutputSignature = [],
        Parameters = [],
    };

    [Fact]
    public void LoneRegisterT1_IsSlot1_NotItsOrdinal0()
    {
        ReflectedEffect reflected = Effect([("Tex1", 33)], [("Tex1Sampler", 33)]);
        reflected.Textures[0].BindSlot.ShouldBe(0, "the reflector's ordinal, the pre-fix slot");

        var result = VulkanResourceSlots.FromBindings(reflected, "", "x.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Textures.Single().BindSlot.ShouldBe(1);
        result.Value.Samplers.Single().BindSlot.ShouldBe(1);
    }

    [Fact]
    public void RegisterGap_T0T2_KeepsTheGap()
    {
        ReflectedEffect reflected = Effect([("Base", 32), ("Overlay", 34)], [("BaseSampler", 32), ("OverlaySampler", 34)]);

        var result = VulkanResourceSlots.FromBindings(reflected, "", "x.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Textures.Select(t => (t.Name, t.BindSlot)).ShouldBe([("Base", 0), ("Overlay", 2)]);
        result.Value.Samplers.Select(s => (s.Name, s.BindSlot)).ShouldBe([("BaseSampler", 0), ("OverlaySampler", 2)]);
    }

    [Fact]
    public void ContiguousFromZero_IsUnchanged()
    {
        // Every shape the binding rewriter numbers itself: the fix is a no-op there.
        ReflectedEffect reflected = Effect([("A", 32), ("B", 33), ("C", 34)], [("SA", 32), ("SB", 33), ("SC", 34)]);

        var result = VulkanResourceSlots.FromBindings(reflected, "", "x.fx");

        result.IsSuccess.ShouldBeTrue();
        result.Value.Textures.Select(t => t.BindSlot).ShouldBe(reflected.Textures.Select(t => t.BindSlot));
        result.Value.Samplers.Select(s => s.BindSlot).ShouldBe(reflected.Samplers.Select(s => s.BindSlot));
    }

    [Fact]
    public void KeepsEverythingButTheSlot()
    {
        ReflectedEffect reflected = Effect([("Tex1", 33)], [("Tex1Sampler", 33)]);

        var result = VulkanResourceSlots.FromBindings(reflected, "", "x.fx");

        result.Value.Textures.Single().ShouldBe(reflected.Textures.Single() with { BindSlot = 1 });
        result.Value.Samplers.Single().ShouldBe(reflected.Samplers.Single() with { BindSlot = 1 });
    }

    [Fact]
    public void BindingBelow32_IsRefusedWithSD0228_AtTheDeclaration()
    {
        // An auto-numbered resource (one the binding rewriter did not pin): DXC numbers it from 0,
        // so binding - 32 is negative, and mgfxc's own arithmetic wraps it to 224+.
        const string source = "#line 1 \"effect.fx\"\n// Texture2D Commented;\nfloat4 x;\nTexture2D A, B;\n";
        ReflectedEffect reflected = Effect([("A", 0)], []);

        var result = VulkanResourceSlots.FromBindings(reflected, source, "fallback.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0228");
        result.Error.File.ShouldBe("effect.fx");
        result.Error.Line.ShouldBe(3);
        result.Error.Column.ShouldBe(11);
        result.Error.Message.ShouldContain("texture 'A'", Case.Sensitive);
        result.Error.Message.ShouldContain("binding 0", Case.Sensitive);
        result.Error.Message.ShouldContain("register(t0)", Case.Sensitive);
    }

    [Fact]
    public void SamplerBelow32_IsRefusedWithSD0228_NamingTheSampler()
    {
        ReflectedEffect reflected = Effect([("Tex", 32)], [("Tex", 32), ("Loose", 1)]);

        var result = VulkanResourceSlots.FromBindings(reflected, "", "fallback.fx");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SD0228");
        result.Error.File.ShouldBe("fallback.fx");
        result.Error.Message.ShouldContain("sampler 'Loose'", Case.Sensitive);
        result.Error.Message.ShouldContain("register(s0)", Case.Sensitive);
    }
}
