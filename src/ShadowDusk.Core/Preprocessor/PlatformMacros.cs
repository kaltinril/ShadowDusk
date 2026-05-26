#nullable enable

namespace ShadowDusk.Core.Preprocessor;

public static class PlatformMacros
{
    public static MacroSet For(PlatformTarget platform) => platform switch
    {
        PlatformTarget.DirectX => new MacroSet([new("MGFX"), new("HLSL"), new("SM4")]),
        PlatformTarget.OpenGL  => new MacroSet([new("MGFX"), new("GLSL"), new("OPENGL")]),
        PlatformTarget.Vulkan  => new MacroSet([new("MGFX"), new("HLSL"), new("VULKAN"), new("SM6")]),
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };
}
