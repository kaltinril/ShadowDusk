#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using ShadowDusk.Core;

namespace ShadowDusk.MgcbPlugin;

/// <summary>
/// Maps MonoGame's <see cref="TargetPlatform"/> (the <c>/platform:</c> line in a <c>.mgcb</c>, or
/// <c>-p</c> / <c>$(MonoGamePlatform)</c> in a 3.8.5 Content Builder) onto a ShadowDusk
/// <see cref="PlatformTarget"/>, and parses the optional <c>ShaderProfile</c> escape hatch.
/// <para>
/// <b>The map keys on the platform's NAME, never on the enum member or its number.</b> MonoGame
/// renumbered <see cref="TargetPlatform"/> in 3.8.5 (<c>Stadia=12, Web=13</c> became
/// <c>Web=12, DesktopVK=13, WindowsDX12=14, XboxSeries=15</c>). This assembly is compiled against
/// the 3.8.2.1105 contract, so switching on members would compile the OLD numbers in: on a
/// 3.8.5 host it turned <c>/platform:DesktopVK</c> into "Web" and silently emitted an OpenGL
/// effect for a Vulkan runtime, and refused <c>Web</c> and <c>WindowsDX12</c> outright. In the
/// host's process the loaded enum type is the host's own, so <c>ToString()</c> yields the name
/// the consumer actually wrote, whatever its number is in that version. Names are the real
/// contract: they are what <c>/platform:</c>, <c>-p</c>, and <c>$(MonoGamePlatform)</c> carry.
/// </para>
/// <para>
/// Pure and side-effect free, so the mapping is unit-testable without an MGCB build, with both
/// numberings fed side by side.
/// </para>
/// </summary>
public static class MgcbPlatformMap
{
    /// <summary>
    /// The platform NAME → ShadowDusk target table. One entry per MonoGame platform ShadowDusk
    /// has a backend for; everything else (the consoles, <c>Stadia</c>, a number with no name
    /// on the host's enum) maps to nothing and fails loudly at the call site.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, PlatformTarget Target)> Table =
    [
        // WindowsDX: the DirectX 11 runtime, the only MGCB platform whose runtime loads DXBC.
        ("Windows",      PlatformTarget.DirectX),

        // Every GL-family runtime loads the SAME OpenGL .mgfx: DesktopGL, macOS, mobile,
        // Raspberry Pi, the WebGL/Web platform, NativeClient. One artifact, no consumer flag.
        ("DesktopGL",    PlatformTarget.OpenGL),
        ("MacOSX",       PlatformTarget.OpenGL),
        ("iOS",          PlatformTarget.OpenGL),
        ("Android",      PlatformTarget.OpenGL),
        ("RaspberryPi",  PlatformTarget.OpenGL),
        ("Web",          PlatformTarget.OpenGL),
        ("NativeClient", PlatformTarget.OpenGL),

        // MonoGame 3.8.5's new native backends, derived from the platform the consumer already
        // picked (the seamless pattern). Both names exist only on a 3.8.5+ host; on older hosts
        // no value stringifies to them and the ShaderProfile escape hatch is the way to reach
        // these targets. DirectX 12 output must be built on Windows (DXIL signing, SD0214).
        ("DesktopVK",    PlatformTarget.Vulkan),
        ("WindowsDX12",  PlatformTarget.DirectX12),
    ];

    /// <summary>
    /// The MonoGame platform names ShadowDusk can build for, in table order. This is what the
    /// <c>SD0501</c> diagnostic lists, so the list is built from the map and can never name a
    /// platform the map refuses.
    /// </summary>
    public static IReadOnlyList<string> SupportedPlatformNames { get; } =
        Table.Select(entry => entry.Name).ToArray();

    /// <summary><see cref="SupportedPlatformNames"/> joined for diagnostic text.</summary>
    public static string SupportedPlatformNamesText { get; } = string.Join(", ", SupportedPlatformNames);

    /// <summary>
    /// The ShadowDusk target MonoGame's platform implies, or <see langword="null"/> when
    /// ShadowDusk has no backend for it. Resolves through the platform's <b>name</b>
    /// (<see cref="FromPlatformName"/>), so it is correct under every <see cref="TargetPlatform"/>
    /// numbering the host might have loaded.
    /// </summary>
    public static PlatformTarget? FromTargetPlatform(TargetPlatform platform)
        => FromPlatformName(platform.ToString());

    /// <summary>
    /// The ShadowDusk target for a MonoGame platform <b>name</b> (<c>"DesktopGL"</c>,
    /// <c>"DesktopVK"</c>, ...), or <see langword="null"/> when ShadowDusk has no backend for it.
    /// Ordinal, case-sensitive: the names are enum member names, which MonoGame spells exactly
    /// one way. Mirrors what MonoGame's own <c>EffectProcessor</c> does with the same names:
    /// <c>Windows</c> is the DirectX 11 runtime, <c>DesktopVK</c> Vulkan, <c>WindowsDX12</c>
    /// DirectX 12, and every other platform MonoGame/KNI ships loads the OpenGL <c>.mgfx</c>.
    /// </summary>
    public static PlatformTarget? FromPlatformName(string platformName)
    {
        ArgumentNullException.ThrowIfNull(platformName);

        foreach ((string name, PlatformTarget target) in Table)
        {
            if (string.Equals(name, platformName, StringComparison.Ordinal))
                return target;
        }

        return null;
    }

    /// <summary>
    /// Parses the optional <c>ShaderProfile</c> processor parameter. Empty/whitespace means
    /// "derive from the platform" (the seamless default). The accepted names are exactly
    /// the ShadowDusk CLI's <c>/Profile:</c> names, so a consumer who already knows one knows
    /// the other. Returns <see langword="false"/> for an unknown name.
    /// </summary>
    public static bool TryParseShaderProfile(string? value, out PlatformTarget target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "directx_11": target = PlatformTarget.DirectX;   return true;
            case "directx_12": target = PlatformTarget.DirectX12; return true;
            case "opengl":     target = PlatformTarget.OpenGL;    return true;
            case "vulkan":     target = PlatformTarget.Vulkan;    return true;
            default:                                              return false;
        }
    }

    /// <summary>The <c>ShaderProfile</c> names accepted, for the diagnostic text.</summary>
    public const string ShaderProfileNames = "DirectX_11, DirectX_12, OpenGL, Vulkan";
}
