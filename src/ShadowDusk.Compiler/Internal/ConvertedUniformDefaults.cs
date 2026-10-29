#nullable enable

using System.Text.RegularExpressions;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;
using ShadowDusk.HLSL;

namespace ShadowDusk.Compiler.Internal;

/// <summary>
/// The declared HLSL default of each uniform a source-emitting converter (SkSL, raylib) declares
/// (issue #435). Neither converted shader can carry the initializer (DXC drops it, and the
/// emitted text declares the uniform without a value), so the default is reported beside the
/// shader instead, read by the SAME machinery the MGFX route's
/// <see cref="ParameterReflection.DefaultValue"/> comes from:
/// <see cref="CompilationPipeline.ResolveParameterDefaults"/> (the initializer reader over the
/// compiler's input, with its <c>SD0017</c> warnings), <see cref="CompilationPipeline.DefaultShapeMismatch"/>
/// and <c>ParameterDefaultValues.ToLogical</c>. The values are therefore the ones the MGFX
/// route reports for the same source, in the same layout.
/// </summary>
internal static class ConvertedUniformDefaults
{
    /// <summary>One uniform the converted shader declares, as the converter emits it.</summary>
    /// <param name="Name">The uniform's name in the converted shader.</param>
    /// <param name="GlslType">Its GLSL/SkSL type (<c>float</c>, <c>vec4</c>, <c>mat4</c>, <c>int</c>, <c>uint</c> ...).</param>
    /// <param name="ArrayLength">The array length, or 0 when it is not an array.</param>
    internal readonly record struct Uniform(string Name, string GlslType, int ArrayLength);

    private static readonly Regex VectorType = new(@"^(?<kind>[iub]?)vec(?<n>[234])$", RegexOptions.Compiled);
    private static readonly Regex MatrixType = new(@"^mat(?<a>[234])(?:x(?<b>[234]))?$", RegexOptions.Compiled);

    /// <summary>
    /// The default of each of <paramref name="uniforms"/>, index for index: the declared default in
    /// <see cref="ParameterReflection.DefaultValue"/>'s layout, or null when the HLSL global has no
    /// initializer, the initializer cannot be evaluated, or the uniform has no HLSL declaration
    /// (a synthesized uniform). An initializer that cannot be evaluated, or whose shape does not
    /// match the converted uniform, adds the <c>SD0017</c> warning the MGFX route gives it.
    /// </summary>
    /// <param name="compilerInput">The text DXC compiled: flattened, technique blocks stripped.</param>
    /// <param name="sourceName">The display name of the source, for diagnostics.</param>
    /// <param name="uniforms">The converted shader's uniforms, in declaration order.</param>
    /// <param name="synthesized">Uniforms the converter invented; they have no HLSL default.</param>
    /// <param name="warnings">Receives the <c>SD0017</c> warnings.</param>
    public static IReadOnlyList<float>?[] Resolve(
        string compilerInput,
        string sourceName,
        IReadOnlyList<Uniform> uniforms,
        IReadOnlyCollection<string> synthesized,
        List<ShaderError> warnings)
    {
        var result = new IReadOnlyList<float>?[uniforms.Count];

        // The HLSL global behind each uniform, and its shape as the converted shader declares it.
        var parameters = new ParameterReflection?[uniforms.Count];
        for (int i = 0; i < uniforms.Count; i++)
        {
            Uniform u = uniforms[i];
            if (synthesized.Contains(u.Name))
                continue;
            parameters[i] = Shape(HlslName(u.Name, compilerInput), u.GlslType, u.ArrayLength);
        }

        List<ParameterReflection> declared = parameters.OfType<ParameterReflection>().ToList();
        if (declared.Count == 0)
            return result;

        IReadOnlyDictionary<string, GlobalInitializerDefault> defaults =
            CompilationPipeline.ResolveParameterDefaults(compilerInput, sourceName, declared, warnings);

        for (int i = 0; i < uniforms.Count; i++)
        {
            if (parameters[i] is not { } p || !defaults.TryGetValue(p.Name, out GlobalInitializerDefault? def))
                continue;

            string? mismatch = CompilationPipeline.DefaultShapeMismatch(p, def);
            if (mismatch is not null)
            {
                warnings.Add(CompilationPipeline.DefaultNotPreserved(def.File, def.Line, def.Column, p.Name, mismatch));
                continue;
            }

            if (p.Class is not (EffectParameterClass.Scalar or EffectParameterClass.Vector or EffectParameterClass.Matrix))
                continue;

            float[]? values = ParameterDefaultValues.ToLogical(
                p.Class, p.Type, def.Rows, def.Columns, def.ElementBlobs, def.Kind);
            if (values is not null)
                result[i] = Array.AsReadOnly(values);
        }
        return result;
    }

    /// <summary>
    /// The HLSL name behind a converted uniform. SPIRV-Cross renames a global whose name is a GLSL
    /// reserved word by prefixing an underscore (<c>input</c> becomes <c>_input</c>); the default
    /// belongs to the name the source declares.
    /// </summary>
    private static string HlslName(string name, string hlsl)
    {
        if (name.Length > 1 && name[0] == '_' && !ContainsWord(hlsl, name) && ContainsWord(hlsl, name[1..]))
            return name[1..];
        return name;
    }

    private static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b");

    /// <summary>
    /// The reflection shape of a converted uniform, in HLSL terms. DXC lays an HLSL
    /// <c>floatRxC</c> out as R SPIR-V columns of C components, so SPIRV-Cross declares it
    /// <c>matRxC</c> (the same two digits); a <c>bool</c> arrives as <c>uint</c>, since SPIR-V
    /// has no bool in a uniform block. A type with no numeric shape (a struct) keeps
    /// <see cref="EffectParameterClass.Struct"/>, so an initializer on it reads as a shape mismatch.
    /// </summary>
    private static ParameterReflection Shape(string name, string glslType, int arrayLength)
    {
        EffectParameterClass cls = EffectParameterClass.Struct;
        EffectParameterType type = EffectParameterType.Void;
        int rows = 1, columns = 1;

        switch (glslType)
        {
            case "float": cls = EffectParameterClass.Scalar; type = EffectParameterType.Single; break;
            case "int" or "uint": cls = EffectParameterClass.Scalar; type = EffectParameterType.Int32; break;
            case "bool": cls = EffectParameterClass.Scalar; type = EffectParameterType.Bool; break;
            default:
                if (VectorType.Match(glslType) is { Success: true } v)
                {
                    cls = EffectParameterClass.Vector;
                    columns = v.Groups["n"].Value[0] - '0';
                    type = v.Groups["kind"].Value switch
                    {
                        "" => EffectParameterType.Single,
                        "b" => EffectParameterType.Bool,
                        _ => EffectParameterType.Int32,
                    };
                }
                else if (MatrixType.Match(glslType) is { Success: true } m)
                {
                    cls = EffectParameterClass.Matrix;
                    type = EffectParameterType.Single;
                    rows = m.Groups["a"].Value[0] - '0';
                    columns = m.Groups["b"].Success ? m.Groups["b"].Value[0] - '0' : rows;
                }
                break;
        }

        return new ParameterReflection
        {
            Name = name,
            Class = cls,
            Type = type,
            Rows = rows,
            Columns = columns,
            Elements = arrayLength,
        };
    }
}
