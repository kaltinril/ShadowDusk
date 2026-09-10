#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using ShadowDusk.Core;
using ShadowDusk.MgcbPlugin;
using Shouldly;
using Xunit;

namespace ShadowDusk.Integration.Tests.MgcbPlugin;

/// <summary>
/// Pins the platform map to platform NAMES (Phase 63 Area B, issue #203). MonoGame renumbered
/// <see cref="TargetPlatform"/> in 3.8.5 (<c>Stadia=12, Web=13</c> became
/// <c>Web=12, DesktopVK=13, WindowsDX12=14, XboxSeries=15</c>); the shipped plugin switched on
/// enum MEMBERS, i.e. on the 3.8.2.1105 numbers it was compiled against, and on a real
/// <c>dotnet-mgcb</c> 3.8.5 turned <c>/platform:DesktopVK</c> into an OpenGL payload under
/// platform byte <c>V</c> (a silent wrong artifact) and refused <c>Web</c> with a message that
/// listed Web as supported.
///
/// <para>This test project compiles against the 3.8.2.1105 contract, so the raw values below
/// stringify with the OLD numbering: <c>(TargetPlatform)12</c> is <c>Stadia</c> and
/// <c>13</c> is <c>Web</c>. The 3.8.5 names are fed as strings side by side, which is exactly
/// what a 3.8.5 host's <c>ToString()</c> produces for 13 and 14. A revert to member matching
/// cannot resolve <c>"DesktopVK"</c> or <c>"WindowsDX12"</c> at all and fails here.</para>
/// </summary>
public sealed class MgcbPlatformMapTests
{
    [Theory]
    [InlineData("Windows",      PlatformTarget.DirectX)]
    [InlineData("DesktopGL",    PlatformTarget.OpenGL)]
    [InlineData("MacOSX",       PlatformTarget.OpenGL)]
    [InlineData("iOS",          PlatformTarget.OpenGL)]
    [InlineData("Android",      PlatformTarget.OpenGL)]
    [InlineData("RaspberryPi",  PlatformTarget.OpenGL)]
    [InlineData("Web",          PlatformTarget.OpenGL)]
    [InlineData("NativeClient", PlatformTarget.OpenGL)]
    // The two 3.8.5-only names: derived from the platform, no ShaderProfile needed.
    [InlineData("DesktopVK",    PlatformTarget.Vulkan)]
    [InlineData("WindowsDX12",  PlatformTarget.DirectX12)]
    public void SupportedNamesMapByName(string name, PlatformTarget expected)
    {
        MgcbPlatformMap.FromPlatformName(name).ShouldBe(expected);
        MgcbPlatformMap.SupportedPlatformNames.ShouldContain(name);
    }

    [Theory]
    [InlineData("Xbox360")]
    [InlineData("PlayStation4")]
    [InlineData("PlayStation5")]
    [InlineData("XboxOne")]
    [InlineData("Switch")]
    [InlineData("Stadia")]
    [InlineData("XboxSeries")]  // new in 3.8.5, a console: refused like the others
    [InlineData("14")]          // a 3.8.5 number seen through a pre-3.8.5 enum: no name, refused loudly
    [InlineData("desktopvk")]   // names are ordinal and case-sensitive, as enum names are
    [InlineData("")]
    public void UnsupportedNamesMapToNothing(string name)
    {
        MgcbPlatformMap.FromPlatformName(name).ShouldBeNull();
        MgcbPlatformMap.SupportedPlatformNames.ShouldNotContain(name);
    }

    /// <summary>
    /// The 3.8.2.1105 numbering this assembly is compiled against, fed as raw values, resolves
    /// through the name each value stringifies to on THIS host - never through the number.
    /// </summary>
    [Theory]
    [InlineData(0,  PlatformTarget.DirectX)]   // Windows
    [InlineData(4,  PlatformTarget.OpenGL)]    // DesktopGL
    [InlineData(12, null)]                     // Stadia on 3.8.2.1105 (Web on 3.8.5)
    [InlineData(13, PlatformTarget.OpenGL)]    // Web on 3.8.2.1105 (DesktopVK on 3.8.5)
    [InlineData(14, null)]                     // unnamed here (WindowsDX12 on 3.8.5)
    [InlineData(15, null)]                     // unnamed here (XboxSeries on 3.8.5)
    public void RawValuesResolveThroughTheHostEnumsName(int rawValue, PlatformTarget? expected)
    {
        var platform = (TargetPlatform)rawValue;

        MgcbPlatformMap.FromTargetPlatform(platform).ShouldBe(expected);
        MgcbPlatformMap.FromTargetPlatform(platform).ShouldBe(
            MgcbPlatformMap.FromPlatformName(platform.ToString()),
            "FromTargetPlatform must be nothing more than FromPlatformName(platform.ToString())");
    }

    /// <summary>
    /// The same raw values under the 3.8.5 numbering, expressed the only way a 3.8.2.1105-compiled
    /// test can: by the name a 3.8.5 host's enum gives each number. This is the DesktopVK case
    /// that shipped broken.
    /// </summary>
    [Theory]
    [InlineData(12, "Web",         PlatformTarget.OpenGL)]
    [InlineData(13, "DesktopVK",   PlatformTarget.Vulkan)]
    [InlineData(14, "WindowsDX12", PlatformTarget.DirectX12)]
    [InlineData(15, "XboxSeries",  null)]
    public void The385NumberingResolvesByItsNames(int rawValueOn385, string nameOn385, PlatformTarget? expected)
    {
        MgcbPlatformMap.FromPlatformName(nameOn385).ShouldBe(expected);

        // And the value's meaning under the OLD numbering must NOT leak through: 13 was Web
        // (OpenGL) here, and a member-matching map would return that for a 3.8.5 DesktopVK.
        if (expected == PlatformTarget.Vulkan)
            MgcbPlatformMap.FromTargetPlatform((TargetPlatform)rawValueOn385).ShouldNotBe(PlatformTarget.Vulkan,
                "on this 3.8.2.1105-compiled host value 13 is Web, which proves the number alone cannot identify DesktopVK");
    }

    [Fact]
    public void SupportedListIsBuiltFromTheMapAndNamesEveryMappedPlatform()
    {
        foreach (string name in MgcbPlatformMap.SupportedPlatformNames)
            MgcbPlatformMap.FromPlatformName(name).ShouldNotBeNull($"{name} is listed as supported but does not map");

        MgcbPlatformMap.SupportedPlatformNamesText.ShouldBe(string.Join(", ", MgcbPlatformMap.SupportedPlatformNames));
    }

    /// <summary>
    /// <c>SD0501</c>'s text is built from the map: it names the refused platform once, as the
    /// offender, and never inside the supported list (the 3.8.5 <c>/platform:Web</c> message
    /// used to list Web as supported while refusing it).
    /// </summary>
    [Fact]
    public void Sd0501ListsOnlyPlatformsTheMapAccepts()
    {
        string path = TestHelpers.FixturePath("Grayscale.fx");
        var input = new EffectContent
        {
            Identity   = new ContentIdentity(path, "ShadowDusk"),
            EffectCode = File.ReadAllText(path),
        };

        // Value 14 has no name on this host's enum - the shape a pre-3.8.5 plugin sees for a
        // platform it cannot name. It must be refused loudly, by its number.
        var exception = Should.Throw<InvalidContentException>(
            () => new ShadowDuskEffectProcessor().Process(input, new FakeContentProcessorContext((TargetPlatform)14)));

        exception.Message.ShouldContain("SD0501", Case.Sensitive);
        exception.Message.ShouldContain("platform '14' is not supported", Case.Sensitive);
        exception.Message.ShouldContain("Supported MonoGame platforms: " + MgcbPlatformMap.SupportedPlatformNamesText, Case.Sensitive);
        exception.Message.ShouldContain("DesktopVK", Case.Sensitive);
        exception.Message.ShouldContain("WindowsDX12", Case.Sensitive);
        exception.Message.ShouldNotContain("Stadia", Case.Sensitive);
    }
}
