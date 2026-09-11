#nullable enable

using System;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.Validation.Dx;

/// <summary>
/// Issue #204: pins a MonoGame DirectX device to WARP (Windows' bundled software D3D
/// rasterizer) instead of a hardware adapter, so these render gates can run headless on
/// a GPU-less CI runner (<c>windows-latest</c>). MonoGame reads this static while
/// creating the device and ignores whatever adapter it was otherwise handed, so it must
/// be set before the driver's <see cref="GraphicsDeviceManager"/> is constructed.
///
/// Opt-in via <see cref="EnvVar"/> so the default `dotnet run` a developer uses locally
/// keeps rendering on their real GPU (WARP renders correctly but is much slower, and a
/// dev box's GPU is the reference this whole matrix is validated against).
/// </summary>
public static class DxHeadlessRasterizer
{
    public const string EnvVar = "SHADOWDUSK_DX_WARP";

    public static void PinIfRequested()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) == "1")
            GraphicsAdapter.UseDriverType = GraphicsAdapter.DriverType.FastSoftware;
    }
}
