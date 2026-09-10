// XnbContentLoadGl = the Phase 64 (issue #199) MonoGame DESKTOPGL arm of the XNB Content.Load gate.
//
// THE CLAIM UNDER TEST is validation/XnbContentLoad's, on the most common consumer runtime:
// a DesktopGL game replaces its content pipeline with ShadowDusk and keeps calling
// Content.Load<Effect>("Foo"). Phase 60 gated it on WindowsDX only; Phase 64 measured it on
// DesktopGL by probe (MonoGame 3.8.1.263, 3.8.2.1105, 3.8.5 - all maxd 0 vs mgcb's own
// build) and this driver turns that measurement into a gate on the pinned DesktopGL package.
//
//   1. builds each fixture's .xnb through STOCK dotnet-mgcb /platform:DesktopGL   (mgfxc arm)
//   2. builds the same fixture through ShadowDusk (OpenGL) + XnbWriter             (arm under test)
//   3. loads BOTH with a real MonoGame DesktopGL ContentManager.Load<Effect>(assetName)
//   4. renders both through the identical SpriteBatch path and requires PIXEL-IDENTITY,
//      plus the envelope assertions (identical to mgcb's field for field except the reader
//      name, which must be the XNA-4.0 string; payload must differ from mgcb's).
//
// Invert stands in for the DX gate's SpriteEffect, whose TECHNIQUE() macro-defined technique
// is the open Phase 41 GAP-1 on OpenGL (SD0010).
//
// Run: dotnet tool restore, then dotnet run --project validation/XnbContentLoadGl -c Release
// (a real GL desktop driver is needed; not yet in CI's llvmpipe lane because mgcb 3.8.4.1's
// EffectProcessor compiles through d3dcompiler in-process and is not verified to run on Linux
// without Wine - see docs/validation-matrix.md section 6).

using ShadowDusk.Core;

namespace ShadowDusk.Validation.XnbContentLoad;

internal static class Program
{
    /// <summary>MGCB <c>/platform:DesktopGL</c> -> <see cref="PlatformTarget.OpenGL"/>, on real MonoGame DesktopGL.</summary>
    private static readonly Case[] Cases =
    [
        new("Grayscale.fx",       "DesktopGL", PlatformTarget.OpenGL),
        new("VertexAndPixel.fx",  "DesktopGL", PlatformTarget.OpenGL),
        new("MultiTexture.fx",    "DesktopGL", PlatformTarget.OpenGL),
        new("Invert.fx",          "DesktopGL", PlatformTarget.OpenGL),
    ];

    private static int Main() => XnbContentLoadDriver.Main("MonoGame DesktopGL", "output-xnb-gl", Cases);
}
