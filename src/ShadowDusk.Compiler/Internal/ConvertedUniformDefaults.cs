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
///
/// <para><b>Which HLSL global a uniform is.</b> SPIRV-Cross renames a block member whose name
/// GLSL cannot take (<c>input</c> to <c>_input</c>, <c>a__b</c> to <c>a_b</c>, <c>gl_X</c> to
/// <c>_RESERVED_IDENTIFIER_FIXUP_gl_X</c>, a clash to <c>_input_1</c>), so the name is never
/// guessed back: the SPIR-V DXC produced still names every member as the HLSL declares it
/// (<c>OpMemberName</c>), and SPIRV-Cross keeps the member order, so each GLSL block member is
/// paired with the reflected member at the same index. A reflected global with a default that no
/// converted uniform is paired with gets an <c>SD0017</c> warning, never a silent null.</para>
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

    // A uniform block in SPIRV-Cross's GLSL: `[layout(...)] uniform BLOCK { members } [instance];`.
    private static readonly Regex UniformBlock = new(
        @"^[ \t]*(?:layout\s*\([^)]*\)\s*)?uniform[ \t]+(?<block>\w+)\s*\{(?<members>[^}]*)\}\s*(?<instance>\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // The declared name of one block member: the last identifier before any array dimensions.
    private static readonly Regex MemberName = new(@"(?<name>\w+)\s*(?:\[[^\]]*\]\s*)*$", RegexOptions.Compiled);

    /// <summary>
    /// The default of each of <paramref name="uniforms"/>, index for index: the declared default in
    /// <see cref="ParameterReflection.DefaultValue"/>'s layout, or null when the HLSL global has no
    /// initializer, the initializer cannot be evaluated, or the uniform has no HLSL declaration
    /// (a synthesized uniform). An initializer that cannot be evaluated, whose shape does not match
    /// the converted uniform, or whose global no converted uniform could be paired with, adds the
    /// <c>SD0017</c> warning the MGFX route gives an initializer it cannot keep.
    /// </summary>
    /// <param name="compilerInput">The text DXC compiled: flattened, technique blocks stripped.</param>
    /// <param name="sourceName">The display name of the source, for diagnostics.</param>
    /// <param name="glsl">SPIRV-Cross's GLSL, before the converter's mapper (its uniform blocks intact).</param>
    /// <param name="spirv">The SPIR-V DXC produced, whose member names are the HLSL names.</param>
    /// <param name="uniforms">The converted shader's uniforms, in declaration order.</param>
    /// <param name="synthesized">Uniforms the converter invented; they have no HLSL default.</param>
    /// <param name="warnings">Receives the <c>SD0017</c> warnings.</param>
    public static IReadOnlyList<float>?[] Resolve(
        string compilerInput,
        string sourceName,
        string glsl,
        ReadOnlyMemory<byte> spirv,
        IReadOnlyList<Uniform> uniforms,
        IReadOnlyCollection<string> synthesized,
        List<ShaderError> warnings)
    {
        var result = new IReadOnlyList<float>?[uniforms.Count];
        (IReadOnlyDictionary<string, string> hlslNameOf, IReadOnlyList<string> reflected) = PairMembers(glsl, spirv);

        // The HLSL global behind each uniform, and its shape as the converted shader declares it.
        // A uniform in a block that could not be paired keeps its own name.
        var parameters = new ParameterReflection?[uniforms.Count];
        for (int i = 0; i < uniforms.Count; i++)
        {
            Uniform u = uniforms[i];
            if (synthesized.Contains(u.Name))
                continue;
            parameters[i] = Shape(hlslNameOf.GetValueOrDefault(u.Name, u.Name), u.GlslType, u.ArrayLength);
        }

        // Every reflected global is asked about, paired or not, so an initializer that cannot be
        // evaluated is reported for it too; the unpaired ones never get a value.
        var asked = new List<ParameterReflection>(parameters.OfType<ParameterReflection>());
        var covered = new HashSet<string>(asked.Select(p => p.Name), StringComparer.Ordinal);
        foreach (string name in reflected.Where(n => !covered.Contains(n)).Distinct(StringComparer.Ordinal))
            asked.Add(Shape(name, "", 0));
        if (asked.Count == 0)
            return result;

        IReadOnlyDictionary<string, GlobalInitializerDefault> defaults =
            CompilationPipeline.ResolveParameterDefaults(compilerInput, sourceName, asked, warnings);

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

        foreach ((string name, GlobalInitializerDefault def) in defaults)
        {
            if (!covered.Contains(name))
            {
                warnings.Add(CompilationPipeline.DefaultNotPreserved(def.File, def.Line, def.Column, name,
                    "the converted shader declares no uniform ShadowDusk could pair with this global, so no " +
                    "default is reported for it"));
            }
        }
        return result;
    }

    /// <summary>
    /// GLSL member name to HLSL name for every member of every uniform block that pairs with a
    /// reflected constant buffer, and every reflected member's HLSL name. A block pairs with the
    /// reflected buffer whose name it carries (DXC names the struct <c>type.NAME</c> and the
    /// variable <c>NAME</c>; SPIRV-Cross spells them <c>type_NAME</c> and <c>NAME</c>, punctuation
    /// replaced) and whose member count it has; failing that, blocks and buffers pair in order when
    /// every count agrees. A block that pairs with nothing maps nothing.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> HlslNameOf, IReadOnlyList<string> Reflected) PairMembers(
        string glsl, ReadOnlyMemory<byte> spirv)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Result<ReflectedEffect, ShaderError> reflection = new SpirvReflector().Reflect(spirv);
        if (reflection.IsFailure)
            return (map, []);

        IReadOnlyList<ConstantBufferReflection> buffers = reflection.Value.ConstantBuffers;
        var reflected = buffers.SelectMany(b => b.Variables.Select(v => v.Name)).ToList();

        var blocks = UniformBlock.Matches(glsl)
            .Select(m => (
                Block: m.Groups["block"].Value,
                Instance: m.Groups["instance"].Value,
                Members: m.Groups["members"].Value.Split(';')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .Select(s => MemberName.Match(s) is { Success: true } n ? n.Groups["name"].Value : "")
                    .ToList()))
            .ToList();

        var used = new HashSet<int>();
        var pairs = new List<(List<string> Glsl, ConstantBufferReflection Buffer)>();
        foreach (var block in blocks)
        {
            int match = -1;
            for (int b = 0; b < buffers.Count && match < 0; b++)
            {
                if (used.Contains(b) || buffers[b].Variables.Count != block.Members.Count)
                    continue;
                string name = Letters(buffers[b].Name);
                if (Letters(block.Block) == "type" + name || (block.Instance.Length > 0 && Letters(block.Instance) == name))
                    match = b;
            }
            if (match >= 0)
            {
                used.Add(match);
                pairs.Add((block.Members, buffers[match]));
            }
        }

        if (pairs.Count < blocks.Count && blocks.Count == buffers.Count
            && blocks.Select(b => b.Members.Count).SequenceEqual(buffers.Select(b => b.Variables.Count)))
        {
            pairs.Clear();
            for (int b = 0; b < blocks.Count; b++)
                pairs.Add((blocks[b].Members, buffers[b]));
        }

        foreach ((List<string> members, ConstantBufferReflection buffer) in pairs)
        {
            for (int m = 0; m < members.Count; m++)
            {
                if (members[m].Length > 0)
                    map[members[m]] = buffer.Variables[m].Name;
            }
        }
        return (map, reflected);
    }

    private static string Letters(string name) => new(name.Where(char.IsLetterOrDigit).ToArray());

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
