#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Selects which backend compiles HLSL to SM5 DXBC for the DirectX target.
/// </summary>
public enum DxbcBackend
{
    /// <summary>
    /// d3dcompiler_47 (the fxc engine) — the Windows-only correctness "oracle", proven
    /// to load in real MonoGame.Framework.WindowsDX and match mgfxc pixel-for-pixel.
    /// Opt-in (it hard-fails off Windows); the default is <see cref="Vkd3d"/>.
    /// </summary>
    D3DCompiler = 0,

    /// <summary>
    /// vkd3d-shader — the cross-platform shipping DXBC backend (Linux/macOS/Windows, no
    /// Wine or Windows SDK), validated against the d3dcompiler_47 oracle (Phase 18).
    /// Default: host-independent, so the same source produces the same bytes on every OS.
    /// </summary>
    Vkd3d = 1,
}
