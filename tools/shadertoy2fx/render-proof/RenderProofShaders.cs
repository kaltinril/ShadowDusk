#nullable enable

using System;
using Microsoft.Xna.Framework;
using ShadowDusk.ShaderToy.Runtime;

namespace ShadowDusk.ShaderToy.RenderProof;

/// <summary>
/// A single pixel-value expectation, addressed in DISPLAYED-IMAGE coordinates: (0,0) is the
/// TOP-LEFT of the saved PNG, (W-1, H-1) the bottom-right. This is the orientation a human sees
/// in an image viewer, so the asserts read like "what you'd see on screen".
/// </summary>
/// <param name="Label">Human-readable location, e.g. "displayed bottom-left".</param>
/// <param name="X">Displayed X (0 = left).</param>
/// <param name="Y">Displayed Y (0 = top).</param>
/// <param name="ExpectedR">Expected red [0,1].</param>
/// <param name="ExpectedG">Expected green [0,1].</param>
/// <param name="ExpectedB">Expected blue [0,1].</param>
/// <param name="Tolerance">Allowed absolute per-channel difference in [0,1].</param>
public sealed record RgbAssertion(
    string Label, int X, int Y, float ExpectedR, float ExpectedG, float ExpectedB, float Tolerance);

/// <summary>
/// The deterministic shaders this driver renders, each paired with an analytic expectation
/// generator (given the render width/height). These are the REAL automated gate: a green run
/// means the converted shader rendered the mathematically-correct pixels in the right orientation.
/// </summary>
public static class RenderProofShaders
{
    /// <summary>The constant color a host drives into the <c>custom_uniform_color</c> proof shader.</summary>
    public static readonly Vector3 CustomColor = new(0.25f, 0.50f, 0.75f);

    /// <summary>
    /// (shader-name, asserter, custom-setup) tuples. The asserter receives (width, height) and returns
    /// the expected pixels at chosen probe locations; the optional custom-setup drives any consumer-owned
    /// custom uniforms the shader declares (host-supplied effect parameters).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, Func<int, int, RgbAssertion[]> Asserter, Action<ShaderToyEffect>? CustomSetup)> Catalog =
        new (string, Func<int, int, RgbAssertion[]>, Action<ShaderToyEffect>?)[]
        {
            ("gradient_uv", GradientUvAsserts, null),
            ("radial_distance", RadialAsserts, null),
            ("custom_uniform_color", CustomColorAsserts, e => e.SetCustom("uColor", CustomColor)),
            // G2 plain-GLSL `void main()` mode: SAME analytic gradient + SAME orientation asserts as
            // gradient_uv above. A green run proves gl_FragCoord's Y maps right in main() mode.
            ("main_gradient", GradientUvAsserts, null),
        };

    /// <summary>
    /// custom_uniform_color: fragColor = vec4(uColor, 1) where uColor is a custom uniform the HOST sets.
    /// The whole image must be exactly the host-driven color, proving a consumer-set parameter reflects
    /// through to a valid effect parameter and renders. Orientation-independent (constant fill).
    /// </summary>
    private static RgbAssertion[] CustomColorAsserts(int w, int h)
    {
        const float tol = 0.03f;
        Vector3 c = CustomColor;
        return new[]
        {
            new RgbAssertion("center", w / 2, h / 2, c.X, c.Y, c.Z, tol),
            new RgbAssertion("top-left", 4, 4, c.X, c.Y, c.Z, tol),
            new RgbAssertion("bottom-right", w - 5, h - 5, c.X, c.Y, c.Z, tol),
        };
    }

    /// <summary>
    /// gradient_uv: fragColor = (uv.x, uv.y, 0.5, 1). In ShaderToy's BOTTOM-LEFT fragCoord
    /// convention, uv.y grows upward, so the DISPLAYED image must have:
    ///   - displayed bottom-left  = (R,G) = (0, 0)   [fragCoord (0,0)]
    ///   - displayed bottom-right = (R,G) = (1, 0)
    ///   - displayed top-left     = (R,G) = (0, 1)
    ///   - displayed top-right    = (R,G) = (1, 1)   [fragCoord (W,H)]
    /// If the image is upside-down (bottom-left shows green=1), the harness Y-orientation is wrong.
    /// Blue is a constant 0.5 everywhere.
    /// </summary>
    private static RgbAssertion[] GradientUvAsserts(int w, int h)
    {
        // Probe a few pixels IN from each edge to avoid edge/filtering noise.
        int lo = 4;
        int hiX = w - 1 - 4;
        int hiY = h - 1 - 4;

        // Expected channel value at a displayed pixel: r = x/(w-1); g = (h-1-y)/(h-1) (Y up).
        static float RAt(int x, int w) => x / (float)(w - 1);
        static float GAt(int y, int h) => (h - 1 - y) / (float)(h - 1);

        const float tol = 0.03f; // ~8/255: shader-math + render quantization slack.
        return new[]
        {
            // displayed bottom-left (large Y): should be (0, 0)
            new RgbAssertion("displayed bottom-left", lo, hiY, RAt(lo, w), GAt(hiY, h), 0.5f, tol),
            // displayed top-right (small Y): should be (1, 1)
            new RgbAssertion("displayed top-right", hiX, lo, RAt(hiX, w), GAt(lo, h), 0.5f, tol),
            // displayed top-left: should be (0, 1)
            new RgbAssertion("displayed top-left", lo, lo, RAt(lo, w), GAt(lo, h), 0.5f, tol),
            // displayed bottom-right: should be (1, 0)
            new RgbAssertion("displayed bottom-right", hiX, hiY, RAt(hiX, w), GAt(hiY, h), 0.5f, tol),
            // center: (~0.5, ~0.5)
            new RgbAssertion("center", w / 2, h / 2, RAt(w / 2, w), GAt(h / 2, h), 0.5f, tol),
        };
    }

    /// <summary>
    /// radial_distance: r = clamp(length(uv*2-1)), fragColor = (r, 1-r, 0, 1). Symmetric, so
    /// orientation-independent. Analytic, time-independent:
    ///   - center  -> r = 0     -> (R,G,B) = (0, 1, 0)
    ///   - corners -> r >= 1    -> (R,G,B) = (1, 0, 0)
    /// </summary>
    private static RgbAssertion[] RadialAsserts(int w, int h)
    {
        const float tol = 0.03f;
        // Corner: a few px in, centered-uv magnitude is ~sqrt(2) -> clamped to 1.
        int c = 3;
        return new[]
        {
            new RgbAssertion("center", w / 2, h / 2, 0f, 1f, 0f, tol),
            new RgbAssertion("top-left corner", c, c, 1f, 0f, 0f, tol),
            new RgbAssertion("bottom-right corner", w - 1 - c, h - 1 - c, 1f, 0f, 0f, tol),
        };
    }
}
