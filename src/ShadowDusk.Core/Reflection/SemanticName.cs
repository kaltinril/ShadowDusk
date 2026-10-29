#nullable enable

using System;
using System.Globalization;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// The one spelling of an HLSL semantic that two declarations of the same semantic share:
/// uppercased (semantics are case-insensitive) and with the index taken by VALUE, so
/// <c>texcoord</c>, <c>TEXCOORD0</c> and <c>TEXCOORD00</c> are all <c>TEXCOORD0</c>, as fxc reads them.
/// </summary>
public static class SemanticName
{
    /// <summary>Normalizes <paramref name="semantic"/>; a trailing index too long to parse is kept as written.</summary>
    public static string Normalize(string semantic)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        string upper = semantic.ToUpperInvariant();
        int digits = 0;
        while (digits < upper.Length && char.IsAsciiDigit(upper[^(digits + 1)]))
            digits++;
        string name = upper[..^digits];
        if (digits == 0)
            return name + "0";
        return int.TryParse(upper.AsSpan(upper.Length - digits), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? name + index.ToString(CultureInfo.InvariantCulture)
            : upper;
    }

    /// <summary>The semantic's name without its index (<c>TEXCOORD3</c> is <c>TEXCOORD</c>).</summary>
    public static string BaseName(string semantic)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        string upper = semantic.ToUpperInvariant();
        int end = upper.Length;
        while (end > 0 && char.IsAsciiDigit(upper[end - 1]))
            end--;
        return upper[..end];
    }
}
