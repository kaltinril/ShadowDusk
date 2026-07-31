#nullable enable

using System;
using System.Linq;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Maps an HLSL vertex-input semantic name to MonoGame's <c>VertexElementUsage</c> byte plus
/// its semantic index. Shared by every backend that builds a per-shader vertex attribute table
/// (SPIR-V for Vulkan, DXIL for DirectX12) so the mapping cannot drift between them.
/// </summary>
public static class VertexSemanticMapper
{
    /// <summary>
    /// Semantic → (<c>VertexElementUsage</c> byte, semantic index). The usage values are
    /// MonoGame's <c>VertexElementUsage</c> enum: Position=0, Color=1, TextureCoordinate=2,
    /// Normal=3, Binormal=4, Tangent=5, BlendIndices=6, BlendWeight=7, Depth=8, Fog=9,
    /// PointSize=10, Sample=11, TessellateFactor=12.
    ///
    /// <para>An unrecognised semantic falls back to TextureCoordinate, the same default
    /// mgfxc applies — and, like mgfxc, ShadowDusk warns when it defaults: use the
    /// <see cref="Map(string, out bool)"/> overload and turn a <c>false</c> result into
    /// <see cref="UnrecognizedSemanticWarning"/> (<c>SD0104</c>). The value is never
    /// gated by the warning; the fallback attribute is emitted either way.</para>
    /// </summary>
    public static (byte Usage, int Index) Map(string semantic) => Map(semantic, out _);

    /// <summary>
    /// The same mapping as <see cref="Map(string)"/>, additionally reporting whether the
    /// semantic was actually recognised. The returned usage/index are identical in both
    /// overloads — <paramref name="recognized"/> only says whether they came from the
    /// table or from the TextureCoordinate fallback, so a caller can raise the
    /// mgfxc-parity <c>SD0104</c> warning without changing a single emitted byte.
    /// </summary>
    /// <param name="semantic">The HLSL vertex-input semantic, with or without its numeric index.</param>
    /// <param name="recognized">
    /// <c>true</c> when <paramref name="semantic"/> matched a known semantic (a name in the
    /// table followed by an optional, parseable numeric index); <c>false</c> when the
    /// TextureCoordinate default was applied — including for a table name carrying a numeric
    /// suffix too large to parse (e.g. <c>TEXCOORD</c> followed by 40 digits), which also
    /// takes the fallback path.
    /// </param>
    public static (byte Usage, int Index) Map(string semantic, out bool recognized)
    {
        (string Name, byte Usage)[] known =
        {
            ("SV_POSITION",      0),
            ("POSITION",         0),
            ("COLOR",            1),
            ("TEXCOORD",         2),
            ("NORMAL",           3),
            ("BINORMAL",         4),
            ("TANGENT",          5),
            ("BLENDINDICES",     6),
            ("BLENDWEIGHT",      7),
            ("DEPTH",            8),
            ("FOG",              9),
            ("POINTSIZE",       10),
            // The REAL HLSL vertex semantic for point size is PSIZE (D3D9 era);
            // POINTSIZE is kept as a tolerated alias (bug-hunt 2026-07-27 N5 — PSIZE
            // used to fall through to the TextureCoordinate default and collide with
            // a genuine TEXCOORD0 attribute).
            ("PSIZE",           10),
            ("TESSELLATEFACTOR",12),
        };

        string upper = semantic.ToUpperInvariant();

        foreach ((string name, byte usage) in known)
        {
            if (!upper.StartsWith(name, StringComparison.Ordinal))
                continue;

            string tail = upper[name.Length..];
            // "POSITION" must not swallow a longer unrelated semantic; a match is only valid
            // when what follows is the (optional) numeric semantic index.
            if (tail.Length > 0 && !tail.All(char.IsDigit))
                continue;

            if (tail.Length == 0)
            {
                recognized = true;
                return (usage, 0);
            }
            // TryParse, not Parse: an absurdly long numeric suffix must not throw
            // OverflowException out of a pure mapper (bug-hunt 2026-07-27 N5); it falls
            // through to the unknown-semantic default instead.
            if (int.TryParse(tail, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index))
            {
                recognized = true;
                return (usage, index);
            }
            break;
        }

        // Unknown semantic: default to TextureCoordinate with a trailing-digit index, as mgfxc does.
        recognized = false;
        int digits = 0;
        while (digits < upper.Length && char.IsDigit(upper[^(digits + 1)]))
            digits++;

        return (2, digits > 0 &&
                   int.TryParse(upper[^digits..], System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture, out int unknownIndex)
            ? unknownIndex
            : 0);
    }

    /// <summary>
    /// The <c>SD0104</c> warning for a vertex-input semantic that fell through to the
    /// TextureCoordinate default — mgfxc prints one in exactly this situation, so a
    /// drop-in replacement has to as well. It is a WARNING, never an error: mgfxc accepts
    /// and defaults, and the effect bytes are emitted unchanged either way. The point is
    /// that a typo (<c>TEXCORD0</c> for <c>TEXCOORD0</c>) otherwise silently mints a
    /// phantom TextureCoordinate attribute the consumer's vertex declaration must then
    /// supply, and the only symptom is a failed draw far from the shader.
    /// </summary>
    /// <param name="semantic">The unrecognised semantic exactly as it appeared in the shader.</param>
    /// <param name="index">The semantic index the fallback attribute was written with.</param>
    /// <param name="file">The source file to attribute the warning to; empty when unknown.</param>
    public static ShaderError UnrecognizedSemanticWarning(string semantic, int index, string file = "")
        => new(
            File:     file,
            Line:     0,
            Column:   0,
            Code:     "SD0104",
            Message:  $"Vertex input semantic '{semantic}' is not a recognized HLSL vertex " +
                      $"semantic; it defaults to VertexElementUsage.TextureCoordinate index {index}, " +
                      "the same fallback mgfxc applies (mgfxc warns here too). If this is a typo, " +
                      "the effect will demand a TextureCoordinate element the vertex declaration " +
                      "does not provide, and the draw fails with no reference back to the shader.",
            Severity: ShaderErrorSeverity.Warning);
}
