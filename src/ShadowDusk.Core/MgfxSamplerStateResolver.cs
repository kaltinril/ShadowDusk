#nullable enable

using System.Globalization;

namespace ShadowDusk.Core;

/// <summary>
/// Resolves parsed <c>sampler_state { … }</c> members into the baked
/// <see cref="MgfxSamplerStateInfo"/> mgfxc writes into the <c>.mgfx</c> sampler
/// record (Phase 43, F9). Mirrors mgfxc v3.8.2's <c>TPGParser/SamplerStateInfo</c>
/// exactly:
/// <list type="bullet">
///   <item>State is only present (<c>hasState = 1</c>) when at least one recognized
///         state member is set — a block containing only <c>Texture = &lt;…&gt;</c>
///         resolves to <see langword="null"/>.</item>
///   <item>Defaults are MonoGame's <c>SamplerState</c> constructor values: Wrap
///         addressing, white border color, MaxAnisotropy 4, MaxMipLevel 0, LOD bias 0.</item>
///   <item>The separate Min/Mag/Mip filter members combine into ONE MonoGame
///         <c>TextureFilter</c> via mgfxc's <c>UpdateSamplerState</c> if-chain
///         ("None" treated like "Point"); <c>MipFilter = None</c> additionally forces
///         <c>MipMapLevelOfDetailBias = -16</c> (mgfxc's only mip-disable mechanism).</item>
///   <item>Unrecognized KEYS are ignored (the parser accepted them; fxc tolerates many
///         D3D9 states MonoGame has no analog for), but a recognized key with an
///         unparseable VALUE fails loudly (SD0024) — mgfxc's grammar would reject it,
///         so silently dropping it would diverge from the mgfxc build.</item>
/// </list>
/// </summary>
public static class MgfxSamplerStateResolver
{
    // mgfxc TextureFilterType ordinals (None &lt; Point &lt; Linear &lt; Anisotropic);
    // the comparisons below depend on this ordering, exactly like mgfxc's.
    private enum FilterType { None = 0, Point = 1, Linear = 2, Anisotropic = 3 }

    // MonoGame 3.8.2 TextureFilter ordinals.
    private const byte FilterLinear                    = 0;
    private const byte FilterPoint                     = 1;
    private const byte FilterAnisotropic               = 2;
    private const byte FilterLinearMipPoint            = 3;
    private const byte FilterPointMipLinear            = 4;
    private const byte FilterMinLinearMagPointMipLinear = 5;
    private const byte FilterMinLinearMagPointMipPoint  = 6;
    private const byte FilterMinPointMagLinearMipLinear = 7;
    private const byte FilterMinPointMagLinearMipPoint  = 8;

    /// <summary>
    /// Resolves a sampler's parsed state entries. Returns <c>Ok(null)</c> when no
    /// recognized state member is present.
    /// </summary>
    /// <param name="samplerName">The sampler's name (diagnostics only).</param>
    /// <param name="entries">The raw <c>(key, value)</c> pairs from the
    ///   <c>sampler_state</c> block, in source order.</param>
    /// <param name="sourceFile">The source file (diagnostics only).</param>
    public static Result<MgfxSamplerStateInfo?, ShaderError> Resolve(
        string samplerName,
        IEnumerable<(string Key, string Value)> entries,
        string sourceFile)
    {
        // mgfxc SamplerStateInfo defaults ("NOTE: These match the defaults of SamplerState.")
        var minFilter = FilterType.Linear;
        var magFilter = FilterType.Linear;
        var mipFilter = FilterType.Linear;
        byte addressU = 0, addressV = 0, addressW = 0;              // TextureAddressMode.Wrap
        byte borderR = 255, borderG = 255, borderB = 255, borderA = 255; // Color.White
        int maxAnisotropy = 4;
        int maxMipLevel = 0;
        float lodBias = 0f;
        bool dirty = false;

        foreach ((string rawKey, string rawValue) in entries)
        {
            string key = rawKey.Trim();
            string value = rawValue.Trim();

            if (Is(key, "Texture"))
                continue; // the texture binding, handled by the sampler/texture pairing

            if (Is(key, "MinFilter") || Is(key, "MagFilter") || Is(key, "MipFilter") || Is(key, "Filter"))
            {
                if (!TryParseFilter(value, out FilterType f))
                    return Fail(sourceFile, samplerName, key, value,
                        "supported: None, Point, Linear, Anisotropic");
                if (Is(key, "Filter")) { minFilter = magFilter = mipFilter = f; }
                else if (Is(key, "MinFilter")) minFilter = f;
                else if (Is(key, "MagFilter")) magFilter = f;
                else mipFilter = f;
                dirty = true;
                continue;
            }

            if (Is(key, "AddressU") || Is(key, "AddressV") || Is(key, "AddressW"))
            {
                if (!TryParseAddressMode(value, out byte mode))
                    return Fail(sourceFile, samplerName, key, value,
                        "supported: Wrap, Clamp, Mirror, Border");
                if (Is(key, "AddressU")) addressU = mode;
                else if (Is(key, "AddressV")) addressV = mode;
                else addressW = mode;
                dirty = true;
                continue;
            }

            if (Is(key, "BorderColor"))
            {
                // mgfxc's ParseTreeTools.ParseColor: "0xRRGGBB" (alpha 255) or "0xRRGGBBAA".
                if (!TryParseBorderColor(value, out byte r, out byte g, out byte b, out byte a))
                    return Fail(sourceFile, samplerName, key, value,
                        "expected 0xRRGGBB or 0xRRGGBBAA");
                (borderR, borderG, borderB, borderA) = (r, g, b, a);
                dirty = true;
                continue;
            }

            if (Is(key, "MaxAnisotropy") || Is(key, "MaxMipLevel"))
            {
                // mgfxc's ParseTreeTools.ParseInt: parse as float, floor to int.
                if (!TryParseMgfxcInt(value, out int i))
                    return Fail(sourceFile, samplerName, key, value, "expected a number");
                if (Is(key, "MaxAnisotropy")) maxAnisotropy = i;
                else maxMipLevel = i;
                dirty = true;
                continue;
            }

            if (Is(key, "MipLodBias") || Is(key, "MipMapLodBias"))
            {
                if (!TryParseMgfxcFloat(value, out float f))
                    return Fail(sourceFile, samplerName, key, value, "expected a number");
                lodBias = f;
                dirty = true;
                continue;
            }

            // Unrecognized keys (SRGBTexture, …) are ignored, matching the
            // render-state parser's silently-ignore-unknown-keys policy.
        }

        if (!dirty)
            return Result<MgfxSamplerStateInfo?, ShaderError>.Ok(null);

        // mgfxc's UpdateSamplerState filter combination, verbatim. "None" and "Point"
        // are treated the same here; mip-disable is handled below. When no branch
        // matches (e.g. MagFilter = Anisotropic), the filter keeps SamplerState's
        // default: Linear.
        byte filter = FilterLinear;
        if (minFilter == FilterType.Anisotropic)
            filter = FilterAnisotropic;
        else if (minFilter == FilterType.Linear && magFilter == FilterType.Linear && mipFilter == FilterType.Linear)
            filter = FilterLinear;
        else if (minFilter == FilterType.Linear && magFilter == FilterType.Linear && mipFilter <= FilterType.Point)
            filter = FilterLinearMipPoint;
        else if (minFilter == FilterType.Linear && magFilter <= FilterType.Point && mipFilter == FilterType.Linear)
            filter = FilterMinLinearMagPointMipLinear;
        else if (minFilter == FilterType.Linear && magFilter <= FilterType.Point && mipFilter <= FilterType.Point)
            filter = FilterMinLinearMagPointMipPoint;
        else if (minFilter <= FilterType.Point && magFilter == FilterType.Linear && mipFilter == FilterType.Linear)
            filter = FilterMinPointMagLinearMipLinear;
        else if (minFilter <= FilterType.Point && magFilter == FilterType.Linear && mipFilter <= FilterType.Point)
            filter = FilterMinPointMagLinearMipPoint;
        else if (minFilter <= FilterType.Point && magFilter <= FilterType.Point && mipFilter <= FilterType.Point)
            filter = FilterPoint;
        else if (minFilter <= FilterType.Point && magFilter <= FilterType.Point && mipFilter == FilterType.Linear)
            filter = FilterPointMipLinear;

        // MipFilter = None disables mipmapping the only way mgfxc can: a -16 LOD bias.
        if (mipFilter == FilterType.None)
        {
            lodBias = -16.0f;
            maxMipLevel = 0;
        }

        return Result<MgfxSamplerStateInfo?, ShaderError>.Ok(new MgfxSamplerStateInfo(
            AddressU: addressU,
            AddressV: addressV,
            AddressW: addressW,
            BorderColorR: borderR,
            BorderColorG: borderG,
            BorderColorB: borderB,
            BorderColorA: borderA,
            Filter: filter,
            MaxAnisotropy: maxAnisotropy,
            MaxMipLevel: maxMipLevel,
            MipMapLevelOfDetailBias: lodBias));
    }

    private static bool Is(string key, string name) =>
        string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFilter(string value, out FilterType filter)
    {
        filter = value.ToUpperInvariant() switch
        {
            "NONE"        => FilterType.None,
            "POINT"       => FilterType.Point,
            "LINEAR"      => FilterType.Linear,
            "ANISOTROPIC" => FilterType.Anisotropic,
            _             => (FilterType)(-1),
        };
        return (int)filter != -1;
    }

    private static bool TryParseAddressMode(string value, out byte mode)
    {
        // MonoGame TextureAddressMode ordinals: Wrap=0, Clamp=1, Mirror=2, Border=3.
        int parsed = value.ToUpperInvariant() switch
        {
            "WRAP"   => 0,
            "CLAMP"  => 1,
            "MIRROR" => 2,
            "BORDER" => 3,
            _        => -1,
        };
        mode = (byte)Math.Max(parsed, 0);
        return parsed != -1;
    }

    private static bool TryParseBorderColor(string value, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 0;
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return false;

        // mgfxc ParseTreeTools.ParseColor, verbatim: 8 chars incl. "0x" = RRGGBB
        // (alpha 255); 10 chars = RRGGBBAA.
        if (value.Length == 8)
        {
            r = (byte)((hex >> 16) & 0xFF);
            g = (byte)((hex >> 8)  & 0xFF);
            b = (byte)(hex         & 0xFF);
            a = 255;
            return true;
        }
        if (value.Length == 10)
        {
            r = (byte)((hex >> 24) & 0xFF);
            g = (byte)((hex >> 16) & 0xFF);
            b = (byte)((hex >> 8)  & 0xFF);
            a = (byte)(hex         & 0xFF);
            return true;
        }
        return false;
    }

    private static bool TryParseMgfxcFloat(string value, out float result)
    {
        // mgfxc ParseTreeTools.ParseFloat: strip whitespace and a trailing f/F.
        string s = value.Replace(" ", "").TrimEnd('f', 'F');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseMgfxcInt(string value, out int result)
    {
        result = 0;
        if (!TryParseMgfxcFloat(value, out float f))
            return false;
        result = (int)Math.Floor(f);
        return true;
    }

    // SD0024 — sampler_state member with an unparseable value (registered in
    // docs/error-codes.md). Distinct from SD0011 (render-state value).
    private static Result<MgfxSamplerStateInfo?, ShaderError> Fail(
        string sourceFile, string samplerName, string key, string value, string hint) =>
        Result<MgfxSamplerStateInfo?, ShaderError>.Fail(new ShaderError(
            File: sourceFile,
            Line: 0,
            Column: 0,
            Code: "SD0024",
            Message: $"sampler '{samplerName}' state '{key}' has unrecognized value '{value}' — {hint}"));
}
