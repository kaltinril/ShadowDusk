#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

internal static class DxcFlagBuilder
{
    public static IReadOnlyList<string> Build(
        PlatformTarget platform,
        ShaderStage stage,
        string entryPoint,
        IReadOnlyList<(string Name, string? Value)> macros,
        DxcCompileOptions? options = null)
    {
        options ??= new DxcCompileOptions();

        var args = new List<string>();

        args.Add("-E");
        args.Add(entryPoint);

        string profile;
        string[] platformFlags;

        switch ((platform, stage))
        {
            case (PlatformTarget.OpenGL, ShaderStage.Vertex):
                profile = "vs_5_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.OpenGL, ShaderStage.Pixel):
                profile = "ps_5_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1" };
                break;

            case (PlatformTarget.Vulkan, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-invert-y", "-fvk-use-dx-position-w", "-fspv-reflect" };
                break;

            case (PlatformTarget.Vulkan, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1", "-fspv-reflect" };
                break;

            // DXC does not support SM5 DXBC output; minimum profile is SM6 (DXIL).
            case (PlatformTarget.DirectX, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = Array.Empty<string>();
                break;

            case (PlatformTarget.DirectX, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = Array.Empty<string>();
                break;

            // Metal goes through SPIR-V so use the same profile/flags as OpenGL
            case (PlatformTarget.Metal, ShaderStage.Vertex):
                profile = "vs_5_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.Metal, ShaderStage.Pixel):
                profile = "ps_5_0";
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1" };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(platform), $"Unsupported platform/stage: {platform}/{stage}");
        }

        args.Add("-T");
        args.Add(profile);

        args.AddRange(platformFlags);

        args.Add("-Zpr");

        if (!options.AllowWarnings)
            args.Add("-WX");

        if (options.EmbedDebugInfo)
        {
            args.Add("-Zi");
            args.Add("-Qembed_debug");
        }

        foreach ((string name, string? value) in macros)
        {
            if (value is null)
                args.Add($"-D{name}");
            else
                args.Add($"-D{name}={value}");
        }

        return args;
    }
}
