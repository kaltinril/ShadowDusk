#nullable enable

using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// Projects the parameters of an fx_2_0 effect (the FNA target) onto
/// <see cref="ParameterReflection"/>, the same shape the MGFX targets report (issue #413).
/// </summary>
internal static class Fx2ParameterReflection
{
    /// <summary>
    /// The default is read off the source with the same initializer reader as the MGFX targets
    /// (issue #422) when the shape agrees with the compiled parameter. A scalar or vector that
    /// reader has no value for takes the constant table's (one whole register, so never partly
    /// trimmed). A matrix or array the reader cannot evaluate has no default: the compiler trims
    /// the table to the registers the shader reads, so its value would be partly zeros the author
    /// never wrote. Because of that trimming the reported default can be larger than what the
    /// <c>.fxb</c> file carries: <c>float Weights[3]</c> read only as <c>Weights[1]</c> reflects as
    /// 0.75, 1.75, 2.75 while the file holds 0.75, 1.75, 0. Annotations are not projected (the fx_2_0
    /// writer takes none), so FNA parameters list none.
    /// </summary>
    public static IReadOnlyList<ParameterReflection> From(
        IReadOnlyList<Fx2Parameter> parameters, string flattenedSource, string sourceFile)
    {
        var result = new List<ParameterReflection>(parameters.Count);
        foreach (Fx2Parameter p in parameters)
        {
            bool isObject = p.Class == 4;
            int expected = p.Rows * p.Columns * Math.Max(1, p.Elements);
            result.Add(new ParameterReflection
            {
                Name     = p.Name,
                Semantic = p.Semantic,
                Class    = p.Class switch
                {
                    0 => EffectParameterClass.Scalar,
                    1 => EffectParameterClass.Vector,
                    2 or 3 => EffectParameterClass.Matrix,
                    _ => EffectParameterClass.Object,
                },
                // A sampler (type 10-14) is reported as the Texture type, as the MGFX targets do.
                Type     = isObject
                    ? (p.Type is >= 5 and <= 9 ? (EffectParameterType)p.Type : EffectParameterType.Texture)
                    : (EffectParameterType)p.Type,
                Rows     = isObject ? 0 : p.Rows,
                Columns  = isObject ? 0 : p.Columns,
                Elements = isObject ? 0 : p.Elements,
                DefaultValue = !isObject && p.Rows == 1 && p.Elements == 0 && p.DefaultValue is { } d && d.Count == expected
                    ? Array.AsReadOnly(d.ToArray()) : null,
            });
        }

        // Throwaway warnings: the FNA route reports no SD0017 (its defaults are the constant table's).
        IReadOnlyDictionary<string, GlobalInitializerDefault> fromSource = CompilationPipeline.ResolveParameterDefaults(
            flattenedSource, sourceFile, result, new List<ShaderError>(), compilerIsDxc: false);
        for (int i = 0; i < result.Count; i++)
        {
            ParameterReflection p = result[i];
            if (!fromSource.TryGetValue(p.Name, out GlobalInitializerDefault? def)
                || p.Class == EffectParameterClass.Object
                || CompilationPipeline.DefaultShapeMismatch(p, def) is not null
                || ParameterDefaultValues.ToLogical(p.Class, p.Type, def.Rows, def.Columns, def.ElementBlobs, def.Kind) is not { } values)
                continue;
            result[i] = p with { DefaultValue = Array.AsReadOnly(values) };
        }
        return result.AsReadOnly();
    }
}
