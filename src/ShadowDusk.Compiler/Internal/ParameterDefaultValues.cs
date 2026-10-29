#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;

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
    /// <paramref name="declared"/> (the initializers read off the source, by name) says how an
    /// <c>Int32</c> word reads (issue #427): reflection types <c>int</c>, <c>uint</c> and, on Vulkan,
    /// <c>bool</c> all as <c>Int32</c>, so a declared <c>bool</c> reads as 0 or 1 and a declared
    /// <c>uint</c> as its unsigned value. Only the reflected value is affected, never the blob.
    /// </summary>
    public static IReadOnlyList<ParameterReflection> Attach(
        IReadOnlyList<ParameterReflection> parameters, IReadOnlyList<EffectParameterInfo> infos,
        IReadOnlyDictionary<string, GlobalInitializerDefault>? declared)
    {
        var result = new ParameterReflection[parameters.Count];
        for (int i = 0; i < result.Length; i++)
        {
            ParameterReflection p = parameters[i];
            GlobalInitializerKind? kind =
                declared is not null && declared.TryGetValue(p.Name, out GlobalInitializerDefault? d) ? d.Kind : null;
            result[i] = i < infos.Count && ToLogical(p, infos[i], kind) is { } values
                ? p with { DefaultValue = Array.AsReadOnly(values) }
                : p;
        }
        return Array.AsReadOnly(result);
    }

    private static float[]? ToLogical(ParameterReflection p, EffectParameterInfo info, GlobalInitializerKind? kind)
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

        // A row_major matrix's blob is in register order (CompilationPipeline.InStorageOrder):
        // Data[j * Rh + k] = M[j][k] for the rows the upload reaches, with header Rh x Ch, which on
        // Vulkan is the declaration's C x R and on DirectX its R x C. Read it back to the declaration.
        if (p.IsRowMajor && p.Class == EffectParameterClass.Matrix)
        {
            int rh = info.RowCount, ch = info.ColumnCount;
            return FromRowMajorStorage(p.Type, rh, ch,
                rows: p.IsHeaderTransposed ? ch : rh, columns: p.IsHeaderTransposed ? rh : ch, blobs, kind);
        }

        return ToLogical(p.Class, p.Type, info.RowCount, info.ColumnCount, blobs, kind);
    }

    private static float[]? FromRowMajorStorage(
        EffectParameterType type, int rh, int ch, int rows, int columns, IReadOnlyList<byte[]> blobs,
        GlobalInitializerKind? kind)
    {
        var values = new float[rows * columns * blobs.Count];
        int at = 0;
        foreach (byte[] blob in blobs)
        {
            if (blob.Length != rh * ch * 4)
                return null;
            for (int j = 0; j < rows; j++)
                for (int k = 0; k < columns; k++)
                    values[at++] = j < ch && k < rh ? ReadComponent(blob, j * rh + k, type, kind) : 0f;
        }
        return values;
    }

    /// <summary>
    /// The logical row-major values of <paramref name="blobs"/> (one default blob per array element,
    /// a single one when not an array), or null when a blob is not <c>rows * columns * 4</c> bytes.
    /// A matrix blob is column by column (<c>Data[c * rows + r] = M[r][c]</c>). <paramref name="declared"/>
    /// is the declaration's scalar kind when known; it decides how an <c>Int32</c> word reads.
    /// </summary>
    public static float[]? ToLogical(
        EffectParameterClass cls, EffectParameterType type, int rows, int columns, IReadOnlyList<byte[]> blobs,
        GlobalInitializerKind? declared)
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
                    values[at++] = ReadComponent(blob, source, type, declared);
                }
            }
        }
        return values;
    }

    /// <summary>
    /// One 32-bit component of a blob as a float: an int by value, a uint by its unsigned value, a bool
    /// as 0 or 1. An <c>Int32</c> parameter is read by its declared kind when that is known (issue #427).
    /// </summary>
    private static float ReadComponent(byte[] blob, int index, EffectParameterType type, GlobalInitializerKind? declared)
    {
        int offset = index * 4;
        if (type == EffectParameterType.Int32 && declared is GlobalInitializerKind.Bool)
            return BitConverter.ToInt32(blob, offset) != 0 ? 1f : 0f;
        if (type == EffectParameterType.Int32 && declared is GlobalInitializerKind.UInt)
            return BitConverter.ToUInt32(blob, offset);
        return type switch
        {
            EffectParameterType.Int32 => BitConverter.ToInt32(blob, offset),
            EffectParameterType.Bool => BitConverter.ToInt32(blob, offset) != 0 ? 1f : 0f,
            _ => BitConverter.ToSingle(blob, offset),
        };
    }
}
