#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Core;

/// <summary>
/// The closed HLSL Shader Model 6 wave/quad intrinsic vocabulary, and the one rejection every
/// route emits when such an intrinsic targets Vulkan (<c>SD0218</c>). Shared so the <c>.fx</c>
/// route (<c>DxcShaderCompiler</c>) and the real-slangc <c>.slang</c> route
/// (<c>SlangCompiler</c>) reject the same construct with the same code and message.
///
/// <para><b>Why Vulkan rejects them</b> (measured, issue #229): MonoGame's DesktopVK runtime
/// creates its instance with <c>VK_API_VERSION_1_0</c> and enables no subgroup extension. These
/// intrinsics need SPIR-V 1.3 and the <c>GroupNonUniform*</c> capabilities, which the Khronos
/// validation layer rejects on that instance (<c>VUID-VkShaderModuleCreateInfo-pCode-08737</c>,
/// <c>-08740</c>, <c>VUID-RuntimeSpirv-None-06343</c>). A CPU driver rendered such a shader
/// correctly anyway, but a GPU driver is within spec to refuse or miscompile it, so emitting it
/// would ship output that works on some players' machines and not others.</para>
/// </summary>
internal static partial class WaveQuadIntrinsics
{
    /// <summary>The diagnostic code for a wave/quad intrinsic on the Vulkan target.</summary>
    public const string VulkanUnsupportedCode = "SD0218";

    // Microsoft's SM6.0 "Wave Intrinsics" group plus the SM6.0 Quad intrinsics that share the
    // same subgroup capability requirement. Kept as the single source of truth for the pattern
    // below; a test asserts the two stay in sync, since a source-generated regex's pattern
    // argument must be a literal.
    public static readonly string[] Names =
    [
        "WaveActiveAllEqual", "WaveActiveAllTrue", "WaveActiveAnyTrue", "WaveActiveBallot",
        "WaveActiveBitAnd", "WaveActiveBitOr", "WaveActiveBitXor", "WaveActiveCountBits",
        "WaveActiveMax", "WaveActiveMin", "WaveActiveProduct", "WaveActiveSum",
        "WaveGetLaneCount", "WaveGetLaneIndex", "WaveIsFirstLane", "WaveMatch",
        "WaveMultiPrefixBitAnd", "WaveMultiPrefixBitOr", "WaveMultiPrefixBitXor",
        "WaveMultiPrefixCountBits", "WaveMultiPrefixProduct", "WaveMultiPrefixSum",
        "WavePrefixCountBits", "WavePrefixProduct", "WavePrefixSum",
        "WaveReadLaneAt", "WaveReadLaneFirst",
        "QuadReadAcrossX", "QuadReadAcrossY", "QuadReadAcrossDiagonal", "QuadReadLaneAt",
        "QuadAny", "QuadAll",
    ];

    [GeneratedRegex(
        @"\b(?:WaveActiveAllEqual|WaveActiveAllTrue|WaveActiveAnyTrue|WaveActiveBallot|" +
        @"WaveActiveBitAnd|WaveActiveBitOr|WaveActiveBitXor|WaveActiveCountBits|" +
        @"WaveActiveMax|WaveActiveMin|WaveActiveProduct|WaveActiveSum|" +
        @"WaveGetLaneCount|WaveGetLaneIndex|WaveIsFirstLane|WaveMatch|" +
        @"WaveMultiPrefixBitAnd|WaveMultiPrefixBitOr|WaveMultiPrefixBitXor|" +
        @"WaveMultiPrefixCountBits|WaveMultiPrefixProduct|WaveMultiPrefixSum|" +
        @"WavePrefixCountBits|WavePrefixProduct|WavePrefixSum|" +
        @"WaveReadLaneAt|WaveReadLaneFirst|" +
        @"QuadReadAcrossX|QuadReadAcrossY|QuadReadAcrossDiagonal|QuadReadLaneAt|" +
        @"QuadAny|QuadAll)\b")]
    private static partial Regex Pattern();

    /// <summary>
    /// The first wave/quad intrinsic found as a whole identifier in <paramref name="text"/>,
    /// with its 1-based line, or <c>null</c> when none appears.
    /// </summary>
    public static (string Name, int Line)? FindFirst(string text)
    {
        Match m = Pattern().Match(text);
        if (!m.Success)
            return null;

        int line = 1;
        for (int i = 0; i < m.Index; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return (m.Value, line);
    }

    /// <summary>
    /// The <c>SD0218</c> message for <paramref name="intrinsic"/> on the Vulkan target
    /// (<c>null</c> when the caller could not identify which intrinsic it was).
    /// </summary>
    public static string VulkanUnsupportedMessage(string? intrinsic) =>
        $"{(intrinsic is null ? "A Shader Model 6 wave/quad intrinsic" : $"'{intrinsic}' is a Shader Model 6 wave/quad intrinsic and")} " +
        "is not supported on the Vulkan target: MonoGame's DesktopVK runtime creates a Vulkan 1.0 " +
        "instance with no subgroup support, so the SPIR-V 1.3 / GroupNonUniform module it needs is " +
        "out of spec there (the Khronos validation layer rejects it, and a GPU driver may refuse or " +
        "miscompile it). Remove it from the Vulkan build (for example behind '#if !VULKAN'), or " +
        "build for DirectX12, which supports it.";
}
