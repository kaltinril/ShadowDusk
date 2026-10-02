#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Which <see cref="PlatformTarget"/> values the browser/WASM host (<c>ShadowDusk.Wasm</c> and
/// <c>ShadowDusk.Slang.Wasm</c>) can export, and the up-front rejection for the rest (issue #272).
///
/// <para>The browser host runs the same pipeline as the desktop, but only the stages that exist
/// as WebAssembly modules: DXC emitting SPIR-V (OpenGL, Vulkan), SPIRV-Cross, and vkd3d-shader
/// (DirectX 11 DXBC, FNA fx_2_0). It has no DXIL path, so <see cref="PlatformTarget.DirectX12"/>
/// is not a browser export target (docs/the-purpose.md host x target matrix). Before this guard a
/// DirectX12 request ran DXC and then failed in the JS shim with an unregistered
/// <c>X0000: DXC output is not a SPIR-V module</c>; it is now refused before any module is
/// loaded or run, with <c>SD1906</c> naming the target and the host.</para>
///
/// <para><see cref="PlatformTarget.Metal"/> is deliberately passed through: it is unsupported on
/// every host and the shared pipeline already refuses it first, before any stage runs, with
/// <c>SD0200</c>. Keeping that code means a Metal request reads the same on every host instead
/// of looking like a browser-only limit.</para>
/// </summary>
internal static class BrowserHostTargets
{
    /// <summary>The diagnostic code for a target the browser host cannot export.</summary>
    internal const string UnsupportedTargetCode = "SD1906";

    /// <summary>The targets the browser host compiles, in the order the message lists them.</summary>
    internal static readonly IReadOnlyList<PlatformTarget> Supported =
        [PlatformTarget.OpenGL, PlatformTarget.Vulkan, PlatformTarget.DirectX, PlatformTarget.Fna];

    /// <summary>
    /// The target a compile of <paramref name="options"/> actually produces: a set
    /// <see cref="CompilerOptions.Profile"/> wins over <see cref="CompilerOptions.Target"/>, exactly
    /// as the pipeline normalizes it, so a profile cannot route around the check.
    /// </summary>
    internal static PlatformTarget EffectiveTarget(CompilerOptions options) =>
        options.Profile?.GraphicsTarget ?? options.Target;

    /// <summary>
    /// Returns the <c>SD1906</c> error when the browser host cannot export the requested target,
    /// or <see langword="null"/> when the compile may proceed (a supported target, or Metal, which
    /// the shared pipeline refuses with <c>SD0200</c> on every host).
    /// </summary>
    /// <param name="options">The compile request.</param>
    /// <param name="sourceFileName">The source name the diagnostic is reported against.</param>
    internal static ShaderError? Reject(CompilerOptions options, string sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlatformTarget target = EffectiveTarget(options);

        if (target == PlatformTarget.Metal || Supported.Contains(target))
            return null;

        string targetName = Enum.IsDefined(target) ? target.ToString() : $"value {(int)target}";
        string reason = target == PlatformTarget.DirectX12
            ? "The in-browser pipeline has no DXIL path: its DXC module is wired to return SPIR-V only, and DX12 output " +
              "must be validated and signed by dxil.dll, which only exists on Windows. Compile DX12 with " +
              "the desktop library, the CLI or the MGCB plugin on Windows."
            : "It is not a PlatformTarget member ShadowDusk defines.";

        return new ShaderError(
            File: sourceFileName,
            Line: 0,
            Column: 0,
            Code: UnsupportedTargetCode,
            Message: $"PlatformTarget.{targetName} is not supported by the browser/WASM host (ShadowDusk.Wasm). " +
                     reason + " The browser host exports: " + string.Join(", ", Supported) + ".");
    }
}
