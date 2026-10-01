#nullable enable

using System.Text.RegularExpressions;

namespace ShadowDusk.Core;

/// <summary>
/// The closed HLSL Shader Model 6 wave/quad intrinsic vocabulary, and the registered rejections
/// every route emits for it: <c>SD0218</c> on Vulkan (a runtime limit) and <c>SD0624</c> on the
/// targets architecturally capped below SM6 (OpenGL, DirectX 11, FNA). Shared so the <c>.fx</c>
/// route (<c>DxcShaderCompiler</c>, the pipeline's vkd3d/d3dcompiler/FNA stages) and the
/// real-slangc <c>.slang</c> route (<c>SlangCompiler</c>) reject the same construct with the
/// same code and message. DirectX12 compiles them.
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

    /// <summary>The diagnostic code for a wave/quad intrinsic on a target capped below SM6.</summary>
    public const string BelowSm6Code = "SD0624";

    public static bool IsBelowSm6Target(PlatformTarget target) =>
        target is PlatformTarget.OpenGL or PlatformTarget.DirectX or PlatformTarget.Fna;

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

    /// <summary>
    /// The <c>SD0624</c> message for <paramref name="intrinsic"/> on <paramref name="target"/>, a
    /// target whose backend can never represent SM6 HLSL (<c>null</c> intrinsic when the caller
    /// could not identify which one).
    /// </summary>
    public static string BelowSm6Message(string? intrinsic, PlatformTarget target) =>
        $"{(intrinsic is null ? "A Shader Model 6 wave/quad intrinsic" : $"'{intrinsic}' is a Shader Model 6 wave/quad intrinsic and")} " +
        $"is not supported on the {target} target: it compiles through ShadowDusk's pipeline at " +
        "Shader Model 5 or lower and can never represent it (OpenGL: a fixed vs_5_0/ps_5_0 DXC " +
        "profile; DirectX: SM5 DXBC; FNA: SM<=3 fx_2_0). Remove it from this build (for example " +
        "behind a platform '#if'), or build for DirectX12, which supports it.";

    // DXC's wording when a wave op needs a Vulkan target env above its 1.0 default.
    private const string DxcWaveNeedsVulkan11 = "Vulkan 1.1 is required";

    /// <summary>
    /// Relabels a compiler failure that is really a wave/quad intrinsic rejection:
    /// <c>SD0218</c> on Vulkan, <c>SD0624</c> on OpenGL, DirectX and FNA. The compiler's own
    /// file, line and column are kept and its message is appended after
    /// <paramref name="compilerName"/>. Returns <c>null</c> for any other failure, and always for
    /// DirectX12.
    /// </summary>
    /// <remarks>
    /// Recognised by the compiler's own wording, never by scanning the source, so a comment, a
    /// string, or a user function that shadows an intrinsic name on an SM5 target is not
    /// rejected. DXC says "Vulkan 1.1 is required"; vkd3d-shader says
    /// <c>Function "X" is not defined</c>; d3dcompiler says <c>undeclared identifier 'X'</c>.
    /// </remarks>
    public static ShaderError? Relabel(ShaderError error, PlatformTarget target, string compilerName)
    {
        bool vulkan = target == PlatformTarget.Vulkan;
        if (!vulkan && !IsBelowSm6Target(target))
            return null;

        string text = error.RawDiagnostics ?? error.Message;
        string? intrinsic;

        if (text.Contains(DxcWaveNeedsVulkan11, StringComparison.Ordinal))
        {
            // DXC echoes the offending source line; the intrinsic is the first wave/quad
            // identifier in it. When it does not show one, the message says so generically.
            intrinsic = FindFirst(text)?.Name;
        }
        else if (!vulkan)
        {
            Match m = UndefinedIdentifierPattern().Match(text);
            string name = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "";
            if (Array.IndexOf(Names, name) < 0)
                return null;
            intrinsic = name;
        }
        else
        {
            return null;
        }

        return error with
        {
            Code = vulkan ? VulkanUnsupportedCode : BelowSm6Code,
            Message = (vulkan ? VulkanUnsupportedMessage(intrinsic) : BelowSm6Message(intrinsic, target))
                + $" {compilerName}: " + error.Message,
        };
    }

    [GeneratedRegex(@"Function ""(\w+)"" is not defined|undeclared identifier '(\w+)'")]
    private static partial Regex UndefinedIdentifierPattern();
}
