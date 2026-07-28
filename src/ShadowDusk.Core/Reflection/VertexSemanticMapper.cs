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
    /// mgfxc applies. mgfxc also prints a warning when it defaults; ShadowDusk does not yet
    /// (tracked in plan/BUG-HUNT-2026-07-27.md N5 — this mapper is pure and has no warning
    /// channel to thread through).</para>
    /// </summary>
    public static (byte Usage, int Index) Map(string semantic)
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
                return (usage, 0);
            // TryParse, not Parse: an absurdly long numeric suffix must not throw
            // OverflowException out of a pure mapper (bug-hunt 2026-07-27 N5); it falls
            // through to the unknown-semantic default instead.
            if (int.TryParse(tail, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int index))
                return (usage, index);
            break;
        }

        // Unknown semantic: default to TextureCoordinate with a trailing-digit index, as mgfxc does.
        int digits = 0;
        while (digits < upper.Length && char.IsDigit(upper[^(digits + 1)]))
            digits++;

        return (2, digits > 0 &&
                   int.TryParse(upper[^digits..], System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture, out int unknownIndex)
            ? unknownIndex
            : 0);
    }
}
