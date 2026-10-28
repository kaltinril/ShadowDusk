#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// Turns the default blobs the MGFX and KNIFX writers are given (issue #422) into
/// <see cref="ParameterReflection.DefaultValue"/> (issue #413), so the reflection result and the
/// bytes written can never disagree.
/// </summary>
internal static class ParameterDefaultValues
{
    /// <summary>
    /// Returns <paramref name="parameters"/> with <see cref="ParameterReflection.DefaultValue"/> set
    /// from <paramref name="infos"/> (the same list, index for index, that the writers receive). A
    /// parameter whose blob is absent (no initializer, or one that could not be evaluated) keeps a
    /// null default, since its written default is zeros the author never asked for. The returned list
    /// and each default are read-only wrappers, so a consumer cannot cast and mutate them.
    /// </summary>
    public static IReadOnlyList<ParameterReflection> Attach(
        IReadOnlyList<ParameterReflection> parameters, IReadOnlyList<EffectParameterInfo> infos)
    {
        var result = new ParameterReflection[parameters.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ParameterReflection p = parameters[i];
            result[i] = i < infos.Count && ToLogical(p, infos[i]) is { } values
                ? p with { DefaultValue = Array.AsReadOnly(values) }
                : p;
        }
        return Array.AsReadOnly(result);
    }

    private static float[]? ToLogical(ParameterReflection p, EffectParameterInfo info)
    {
        if (p.Class is not (EffectParameterClass.Scalar or EffectParameterClass.Vector or EffectParameterClass.Matrix))
            return null;

        IReadOnlyList<EffectParameterInfo> leaves = info.Elements.Count > 0 ? info.Elements : [info];
        var blobs = new List<byte[]>(leaves.Count);
        foreach (EffectParameterInfo leaf in leaves)
        {
            if (leaf.DefaultValue is not { } blob)
                return null;
            blobs.Add(blob);
        }
        return ToLogical(p.Class, p.Type, info.RowCount, info.ColumnCount, blobs);
    }

    /// <summary>
    /// The logical row-major values of <paramref name="blobs"/> (one default blob per array element,
    /// a single one when not an array), or null when a blob is not <c>rows * columns * 4</c> bytes.
    /// A matrix blob is column by column (<c>Data[c * rows + r] = M[r][c]</c>).
    /// </summary>
    public static float[]? ToLogical(
        EffectParameterClass cls, EffectParameterType type, int rows, int columns, IReadOnlyList<byte[]> blobs)
    {
        var values = new float[rows * columns * blobs.Count];
        int at = 0;
        foreach (byte[] blob in blobs)
        {
            if (blob.Length != rows * columns * 4)
                return null;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    int source = cls == EffectParameterClass.Matrix ? c * rows + r : r * columns + c;
                    values[at++] = ReadComponent(blob, source, type);
                }
            }
        }
        return values;
    }

    /// <summary>One 32-bit component of a blob as a float: an int by value, a bool as 0 or 1.</summary>
    private static float ReadComponent(byte[] blob, int index, EffectParameterType type)
    {
        int offset = index * 4;
        return type switch
        {
            EffectParameterType.Int32 => BitConverter.ToInt32(blob, offset),
            EffectParameterType.Bool => BitConverter.ToInt32(blob, offset) != 0 ? 1f : 0f,
            _ => BitConverter.ToSingle(blob, offset),
        };
    }
}
