#nullable enable

using System;
using System.Globalization;

namespace ShadowDusk.Core;

/// <summary>
/// mgfxc's own numeric-literal parsing for <c>.fx</c> state values, in one place.
///
/// mgfxc's scanner folds a trailing HLSL float suffix into the Number token
/// (<c>[+-]? ?[0-9]?\.?[0-9]+[fF]?</c>) and then hands the token text to
/// <c>ParseTreeTools.ParseFloat</c>, which strips embedded spaces and a trailing
/// <c>f</c>/<c>F</c> before parsing. ShadowDusk's <c>FxLexer</c> keeps the suffix in
/// the token for the same reason, so every consumer of a state value has to reproduce
/// that strip — <c>DepthBias = 0.0001f;</c> is ordinary HLSL that mgfxc compiles.
///
/// This lived in three places and had drifted in two of them (the pass render-state
/// parser and the FNA <c>fx_2_0</c> sampler-state mapper both used a raw
/// <c>float.TryParse</c>, so a suffixed literal failed there while compiling fine on
/// the MGFX path). One implementation so they cannot drift again.
/// </summary>
internal static class MgfxcNumericParse
{
    /// <summary>mgfxc <c>ParseTreeTools.ParseFloat</c>: strip spaces and a trailing f/F.</summary>
    internal static bool TryParseFloat(string value, out float result)
    {
        string s = value.Replace(" ", "").TrimEnd('f', 'F');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// mgfxc <c>ParseTreeTools.ParseInt</c>: parse as a float, then floor. A state written
    /// <c>MaxAnisotropy = 4.0;</c> is accepted and truncated, exactly as mgfxc does.
    /// </summary>
    internal static bool TryParseInt(string value, out int result)
    {
        result = 0;
        if (!TryParseFloat(value, out float f))
            return false;
        result = (int)Math.Floor(f);
        return true;
    }
}
