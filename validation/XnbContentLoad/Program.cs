// XnbContentLoad = the Phase 60 (issue #199) rung-4 gate.
//
// THE CLAIM UNDER TEST: a consumer replaces their content pipeline with ShadowDusk and
// "does not change any lines of code at all" - they keep calling Content.Load<Effect>("Foo").
//
// Nothing short of a real ContentManager can prove that. `new Effect(gd, bytes)` proves the
// PAYLOAD loads (already covered elsewhere); it says nothing about the XNB container, the
// platform-byte whitelist, or the type-reader manifest, which are exactly what Content.Load
// validates and reject-on-mismatch. So this driver:
//
//   1. builds each fixture's .xnb through STOCK dotnet-mgcb          (the mgfxc oracle arm)
//   2. builds the same fixture through ShadowDusk + XnbWriter        (the arm under test)
//   3. drops each in its own content directory and loads BOTH with a real
//      ContentManager.Load<Effect>(assetName)  - no `new Effect`, no hand-parse
//   4. renders both through the identical SpriteBatch path and requires the images to be
//      PIXEL-IDENTICAL.
//
// Step 3 is also the demonstration Phase 60 OQ1/OQ5 demanded: the file is dropped where the
// mgfxc-built .xnb sat, loaded by the name the consumer already uses, with no companion file
// and no consumer code change. Reasoning about that was explicitly not good enough.

using ShadowDusk.Core;

namespace ShadowDusk.Validation.XnbContentLoad;

internal static class Program
{
    /// <summary>MGCB <c>/platform:Windows</c> -> <see cref="PlatformTarget.DirectX"/>, on real MonoGame WindowsDX.</summary>
    private static readonly Case[] Cases =
    [
        new("Grayscale.fx",       "Windows", PlatformTarget.DirectX),
        new("VertexAndPixel.fx",  "Windows", PlatformTarget.DirectX),
        new("MultiTexture.fx",    "Windows", PlatformTarget.DirectX),
        new("SpriteEffect.fx",    "Windows", PlatformTarget.DirectX),
    ];

    private static int Main() => XnbContentLoadDriver.Main("MonoGame WindowsDX", "output-xnb", Cases);
}
