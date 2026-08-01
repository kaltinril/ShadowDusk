#nullable enable

using Microsoft.Xna.Framework.Content.Pipeline;
using ShadowDusk.Core;

namespace ShadowDusk.MgcbPlugin;

/// <summary>
/// Maps MGCB's <see cref="TargetPlatform"/> (the <c>/platform:</c> line in a <c>.mgcb</c>)
/// onto a ShadowDusk <see cref="PlatformTarget"/>, and parses the optional
/// <c>ShaderProfile</c> processor-parameter escape hatch.
/// <para>
/// Pure and side-effect free so the mapping is unit-testable without an MGCB build.
/// The mapping is explicit, never an ordinal cast: the two enums share no ordinals
/// (<c>TargetPlatform.Windows = 0</c> is DirectX, <c>PlatformTarget.OpenGL = 1</c>), and
/// <c>MgfxProfile</c> is a third numbering again.
/// </para>
/// </summary>
internal static class MgcbPlatformMap
{
    /// <summary>
    /// The ShadowDusk target MGCB's platform implies, or <see langword="null"/> when
    /// ShadowDusk has no backend for it (the consoles). Mirrors what MonoGame's own
    /// <c>EffectProcessor</c> does: <c>Windows</c> is the DirectX runtime, and every other
    /// platform MonoGame/KNI ships loads the OpenGL <c>.mgfx</c>.
    /// </summary>
    public static PlatformTarget? FromTargetPlatform(TargetPlatform platform) => platform switch
    {
        // WindowsDX - the only MGCB platform whose runtime loads DXBC.
        TargetPlatform.Windows      => PlatformTarget.DirectX,

        // Every GL-family runtime loads the SAME OpenGL .mgfx: DesktopGL, macOS, mobile,
        // Raspberry Pi, and the WebGL/Web platform. One artifact, no consumer flag.
        TargetPlatform.DesktopGL    => PlatformTarget.OpenGL,
        TargetPlatform.MacOSX       => PlatformTarget.OpenGL,
        TargetPlatform.iOS          => PlatformTarget.OpenGL,
        TargetPlatform.Android      => PlatformTarget.OpenGL,
        TargetPlatform.RaspberryPi  => PlatformTarget.OpenGL,
        TargetPlatform.Web          => PlatformTarget.OpenGL,
        TargetPlatform.NativeClient => PlatformTarget.OpenGL,

        // Xbox360, PlayStation4/5, XboxOne, Switch, Stadia: no ShadowDusk backend. Fail
        // loudly at the call site rather than silently emitting a GL artifact their
        // runtimes cannot load.
        _ => null,
    };

    /// <summary>
    /// Parses the optional <c>ShaderProfile</c> processor parameter. Empty/whitespace means
    /// "derive from <c>/platform:</c>" (the seamless default). The accepted names are exactly
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
