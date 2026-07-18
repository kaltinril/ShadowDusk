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
                profile = ShaderProfiles.Sm5Vertex;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.OpenGL, ShaderStage.Pixel):
                profile = ShaderProfiles.Sm5Pixel;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1" };
                break;

            // -fvk-bind-globals pins the implicit "$Globals" cbuffer (DXC's auto-generated
            // home for loose top-level globals like "float3 _sepiaTone;") to a fixed
            // binding, matching whatever auto-binding-space this stage's other auto-bound
            // resources use. Without it, DXC auto-numbers $Globals wherever it lands next
            // in declaration order (e.g. binding 1 instead of 0 depending on source
            // layout), and MonoGame's native Vulkan pipeline crashes (AccessViolationException
            // in GraphicsDevice_DrawIndexed) the moment that binding isn't 0 - confirmed via
            // a minimal repro (Sepia.fx, 2026-07-18): forcing the cbuffer to an explicit
            // register(b0) fixed the crash outright, isolating the auto-numbered raw binding
            // value (not the descriptor shape, already fixed separately) as the cause.
            case (PlatformTarget.Vulkan, ShaderStage.Vertex):
                profile = "vs_6_0";
                platformFlags = new[]
                {
                    "-spirv", "-fvk-use-dx-layout", "-fvk-invert-y", "-fvk-use-dx-position-w", "-fspv-reflect",
                    "-fvk-t-shift", "32", "all", "-fvk-s-shift", "32", "all",
                    "-fvk-bind-globals", "0", "0",
                };
                break;

            case (PlatformTarget.Vulkan, ShaderStage.Pixel):
                profile = "ps_6_0";
                platformFlags = new[]
                {
                    "-spirv", "-fvk-use-dx-layout", "-auto-binding-space", "1", "-fspv-reflect",
                    "-fvk-t-shift", "32", "all", "-fvk-s-shift", "32", "all",
                    "-fvk-bind-globals", "0", "1",
                };
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
                profile = ShaderProfiles.Sm5Vertex;
                platformFlags = new[] { "-spirv", "-fvk-use-dx-layout", "-fvk-use-dx-position-w" };
                break;

            case (PlatformTarget.Metal, ShaderStage.Pixel):
                profile = ShaderProfiles.Sm5Pixel;
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

    /// <summary>
    /// Builds the DXC argument list for a preprocess-only invocation (<c>-P</c>): expand
    /// includes/macros/conditionals and emit the flat HLSL text. Deliberately carries NO
    /// <c>-E</c>/<c>-T</c> (no entry/profile — preprocessing is stage-agnostic) and NO
    /// <c>-WX</c> (warnings-as-errors must not fail a pure expansion). Only the macro
    /// defines are forwarded so the right <c>#if</c> branch (e.g. <c>SM4</c>) is taken.
    /// </summary>
    public static IReadOnlyList<string> BuildPreprocess(
        IReadOnlyList<(string Name, string? Value)> macros)
    {
        var args = new List<string> { "-P" };

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
