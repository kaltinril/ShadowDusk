#nullable enable

using ShadowDusk.Core;

namespace ShadowDusk.HLSL;

/// <summary>
/// The default shader-profile strings shared by every backend (previously triplicated
/// across <c>D3DCompilerShaderCompiler</c>, <c>Vkd3dCompileContract</c>, and
/// <c>DxcFlagBuilder</c>). One definition keeps the backends' default profiles from
/// silently drifting apart.
/// </summary>
internal static class ShaderProfiles
{
    /// <summary>The default SM5 vertex profile (DXBC backends; DXC's SPIR-V SM5 targets).</summary>
    public const string Sm5Vertex = "vs_5_0";

    /// <summary>The default SM5 pixel profile (DXBC backends; DXC's SPIR-V SM5 targets).</summary>
    public const string Sm5Pixel = "ps_5_0";

    /// <summary>
    /// The default SM5 profile for a DXBC compile of the given stage
    /// (<see cref="Sm5Vertex"/> / <see cref="Sm5Pixel"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">For a non-VS/PS stage.</exception>
    public static string DefaultDxbcProfile(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => Sm5Vertex,
        ShaderStage.Pixel  => Sm5Pixel,
        _ => throw new ArgumentOutOfRangeException(
            nameof(stage), $"Unsupported shader stage for DXBC: {stage}"),
    };
}
