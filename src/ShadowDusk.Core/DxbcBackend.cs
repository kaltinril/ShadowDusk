#nullable enable

namespace ShadowDusk.Core;

/// <summary>
/// Selects which backend compiles HLSL to SM5 DXBC for the DirectX target.
/// </summary>
public enum DxbcBackend
{
    /// <summary>
    /// d3dcompiler_47 (the fxc engine) — the Windows-only "oracle", proven to load
    /// in real MonoGame.Framework.WindowsDX and match mgfxc pixel-for-pixel. Default.
    /// </summary>
    D3DCompiler = 0,

    /// <summary>
    /// vkd3d-shader — the cross-platform DXBC backend (Linux/macOS/Windows, no Wine
    /// or Windows SDK). Opt-in until validated against the oracle.
    /// </summary>
    Vkd3d = 1,
}
