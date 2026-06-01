using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ShadowDusk.ShaderFiddle.Web;

/// <summary>
/// The 10 Phase-17 PS-only SM3 corpus shaders and the by-name parameter values
/// that make each one visible. This is a direct port of
/// <c>validation/Shared/ShaderInputs.cs</c> (KNI keeps the
/// <c>Microsoft.Xna.Framework</c> namespace, so the same code compiles) so the
/// in-browser render is set up identically to the desktop Phase-17 validation.
/// </summary>
public static class WebShaderInputs
{
    /// <summary>The 10 standard post-process shaders, in a fixed order.</summary>
    public static readonly IReadOnlyList<string> ShaderNames = new[]
    {
        "Grayscale", "Invert", "TintShader", "Sepia", "Saturate",
        "Pixelated", "Scanlines", "Fading", "Dots", "Dissolve",
    };

    /// <summary>The shader shown (and rendered) on first load.</summary>
    public const string DefaultShader = "Grayscale";

    /// <summary>
    /// Set every parameter any of the 10 shaders might expose. Null-conditional
    /// so a name absent on a given effect is silently skipped — mirrors the
    /// desktop validation harness exactly so the comparison stays fair.
    /// </summary>
    public static void SetParams(Effect e, Texture2D cat)
    {
        // Texture2D-form shaders (Grayscale/Invert/Pixelated/Fading/TintShader)
        e.Parameters["SpriteTexture"]?.SetValue(cat);

        // TintShader
        e.Parameters["TintColor"]?.SetValue(new Vector4(1f, 0.5f, 0.5f, 1f));

        // Sepia
        e.Parameters["_sepiaTone"]?.SetValue(new Vector3(1.2f, 1.0f, 0.8f));

        // Saturate (BloomThreshold is float4 in the source)
        e.Parameters["BloomThreshold"]?.SetValue(new Vector4(0.25f, 0.25f, 0.25f, 0.25f));
        e.Parameters["BloomIntensity"]?.SetValue(1.5f);
        e.Parameters["BloomSaturation"]?.SetValue(0.8f);

        // Scanlines (fixture-documented defaults that make the effect visible)
        e.Parameters["_attenuation"]?.SetValue(800.0f);
        e.Parameters["_linesFactor"]?.SetValue(0.04f);

        // Dots
        e.Parameters["angle"]?.SetValue(0.5f);
        e.Parameters["scale"]?.SetValue(0.5f);
        e.Parameters["ScreenSize"]?.SetValue(new Vector2(cat.Width, cat.Height));

        // Dissolve (needs a second texture; reuse the cat for it)
        e.Parameters["_dissolveTex"]?.SetValue(cat);
        e.Parameters["_progress"]?.SetValue(0.5f);
        e.Parameters["_dissolveThreshold"]?.SetValue(0.04f);
        e.Parameters["_dissolveThresholdColor"]?.SetValue(new Vector4(1f, 0.5f, 0f, 1f));
    }
}
