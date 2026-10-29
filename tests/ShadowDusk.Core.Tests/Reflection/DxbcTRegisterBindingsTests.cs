#nullable enable

using Shouldly;
using ShadowDusk.Core.Reflection;
using Xunit;
using static ShadowDusk.Core.Tests.Reflection.DxbcSyntheticBlobs;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Pure unit tests for <see cref="DxbcTRegisterBindings"/> over synthetic DXBC containers. Real
/// compiler coverage is <c>DirectX11TextureSlotOrderTests</c> (Integration).
/// </summary>
public sealed class DxbcTRegisterBindingsTests
{
    [Fact]
    public void KeepsEveryTRegisterKind_AndDropsSamplersAndCbuffers()
    {
        byte[] rdef = Rdef(Ps50Target,
            [
                new SynthBinding("S", BindSampler, 0, 0),
                new SynthBinding("A", BindTexture, 4, 1),
                new SynthBinding("Structured", 5, 1, 2),
                new SynthBinding("Raw", 7, 1, 3),
                new SynthBinding("TB", 1, 1, 4),
                new SynthBinding("B", BindTexture, 4, 0),
            ],
            []);
        var bindings = DxbcTRegisterBindings.Read(Container(("RDEF", rdef))).ShouldNotBeNull();
        bindings.Select(b => $"{b.Name}@{b.Slot}").ShouldBe(["A@1", "Structured@2", "Raw@3", "TB@4", "B@0"]);
    }

    [Fact]
    public void NotADxbcContainer_OrNoRdef_IsUnreadable()
    {
        DxbcTRegisterBindings.Read(new byte[64]).ShouldBeNull();
        DxbcTRegisterBindings.Read(Container(("SHDR", new byte[8]))).ShouldBeNull();
    }
}
