#nullable enable

using ShadowDusk.Core.Reflection.Spirv;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Builds the <c>.mgfx</c> per-shader vertex ATTRIBUTE TABLE from a vertex shader's SPIR-V,
/// mirroring real mgfxc's Vulkan writer (<c>ShaderProfile.Vulkan.CreateShader</c>): the inputs
/// are ordered by <c>Location</c>, each semantic maps to a MonoGame <c>VertexElementUsage</c>
/// byte plus its semantic index, and an input that spans several locations (a matrix, an array)
/// contributes one entry per location with a running index.
///
/// <para><b>Faithfulness, not runtime behaviour (issue #145, divergence S1).</b> MonoGame
/// 3.8.5's native Vulkan backend builds its vertex input layout positionally from the
/// <c>VertexDeclaration</c> (<c>MGG_InputLayout_Create</c> sets <c>attrib.location = i</c>) and
/// never reads this table — mgfxc's own writer even calls the <c>name</c>/<c>location</c> fields
/// unused under the native backends. ShadowDusk previously wrote an EMPTY table for Vulkan; it
/// now writes the same one mgfxc does, so the container matches the reference compiler and a
/// future runtime that does consume it is not silently mis-served.</para>
///
/// <para>The <c>Name</c> and <c>Location</c> fields are written as <c>""</c> / <c>0</c> —
/// exactly what mgfxc emits for a Vulkan shader (its GL profile is the one that populates
/// them).</para>
/// </summary>
public static class SpirvVertexInputReflector
{
    /// <summary>
    /// Reads the vertex attribute table from <paramref name="spirvBlob"/>. Returns an empty
    /// list if the blob is not parseable SPIR-V or declares no located inputs — never throws,
    /// because a missing attribute table is not fatal on this backend (see class remarks).
    /// </summary>
    public static IReadOnlyList<MgfxVertexAttributeInfo> Read(ReadOnlyMemory<byte> spirvBlob)
    {
        SpirvModule? module = SpirvModule.TryParse(spirvBlob.Span);
        if (module is null)
            return Array.Empty<MgfxVertexAttributeInfo>();

        IReadOnlyList<(string Semantic, int Location, int LocationCount)> inputs;
        try
        {
            inputs = new SpirvReflectionParser(module).ReflectVertexInputs();
        }
        catch (Exception)
        {
            return Array.Empty<MgfxVertexAttributeInfo>();
        }

        var attributes = new List<MgfxVertexAttributeInfo>(inputs.Count);

        foreach ((string semantic, _, int locationCount) in inputs)
        {
            (byte usage, int baseIndex) = MapSemantic(semantic);

            for (int i = 0; i < locationCount; i++)
            {
                attributes.Add(new MgfxVertexAttributeInfo(
                    Name:     string.Empty,
                    Usage:    usage,
                    Index:    (byte)(baseIndex + i),
                    Location: 0));
            }
        }

        return attributes;
    }

    /// <summary>
    /// Semantic → (<c>VertexElementUsage</c> byte, semantic index). The usage values are
    /// MonoGame's <c>VertexElementUsage</c> enum: Position=0, Color=1, TextureCoordinate=2,
    /// Normal=3, Binormal=4, Tangent=5, BlendIndices=6, BlendWeight=7, Depth=8, Fog=9,
    /// PointSize=10, Sample=11, TessellateFactor=12.
    ///
    /// <para>An unrecognised semantic falls back to TextureCoordinate — deliberately matching
    /// mgfxc, which warns and defaults rather than failing the build. (The OpenGL path throws
    /// instead, because there the table IS load-bearing: MonoGame's GL runtime binds vertex
    /// data through it, so a wrong guess silently mis-binds. On Vulkan the table is inert.)</para>
    /// </summary>
    private static (byte Usage, int Index) MapSemantic(string semantic)
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

            return (usage, tail.Length == 0 ? 0 : int.Parse(tail, System.Globalization.CultureInfo.InvariantCulture));
        }

        // Unknown semantic: default to TextureCoordinate with a trailing-digit index, as mgfxc does.
        int digits = 0;
        while (digits < upper.Length && char.IsDigit(upper[^(digits + 1)]))
            digits++;

        return (2, digits == 0
            ? 0
            : int.Parse(upper[^digits..], System.Globalization.CultureInfo.InvariantCulture));
    }
}
