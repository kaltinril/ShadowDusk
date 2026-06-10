#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ShadowDusk.Core;

/// <summary>
/// Emits the D3D9 Effects Framework binary ("fx_2_0", version token 0xFEFF0901) that FNA
/// consumes via FNA3D/MojoShader — the FNA analog of <see cref="MgfxWriter"/>. There is no
/// public spec for this format; the byte layout implemented here follows MojoShader's parser
/// as documented field-by-field in <c>docs/fx2-binary-format.md</c>, cross-checked against
/// real <c>fxc /T fx_2_0</c> output (<c>tests/fixtures/golden/FNA/</c>).
///
/// Layout recap: 8-byte header (token + pool size), then a "data pool" holding every
/// typedef/string/value blob addressed by offsets relative to file offset 8, then the
/// structured stream (counts → parameter records → technique/pass/state records → small /
/// large object sections). MojoShader performs no bounds checking on pool offsets, so this
/// writer is solely responsible for their validity.
/// </summary>
public sealed class Fx2EffectWriter
{
    private const uint Fx2VersionToken = 0xFEFF0901;

    // MOJOSHADER_symbolClass / D3DXPARAMETER_CLASS.
    private const int ClassScalar = 0;
    private const int ClassVector = 1;
    private const int ClassMatrixRows = 2;
    private const int ClassMatrixColumns = 3;
    private const int ClassObject = 4;

    // MOJOSHADER_symbolType / D3DXPARAMETER_TYPE (subset used here).
    private const int TypeBool = 1;
    private const int TypeInt = 2;
    private const int TypeFloat = 3;
    private const int TypeTexture = 5;
    private const int TypePixelShader = 15;
    private const int TypeVertexShader = 16;

    // Pass-state ops (MOJOSHADER_renderStateType file values).
    private const int StateVertexShader = 146;
    private const int StatePixelShader = 147;

    // Sampler-state ops (on-disk 164-based).
    private const int SamplerStateTexture = 164;
    private const int SamplerStateMipMapLodBias = 172;

    // Pass render-state ops FNA's Effect runtime actually applies — every other op makes
    // FNA throw NotImplementedException at load (Effect.cs:604–611; the † set of
    // docs/fx2-binary-format.md §8.2, minus the shader states handled separately).
    private static readonly HashSet<int> FnaHonoredRenderStates =
    [
        0, 1, 3, 6, 7, 8, 9, 13, 22, 23, 24, 25, 26, 27, 28, 29,
        67, 68, 73, 75, 78, 79, 88, 89, 90, 91, 92, 93, 94, 95, 96,
        98, 99, 100, 101, 102,
    ];

    // Sampler-state ops FNA throws NotImplementedException on (Effect.cs:745–748).
    private static readonly HashSet<int> FnaThrowingSamplerStates = [168, 175, 176, 177];

    public Result<byte[], ShaderError> Write(Fx2EffectDesc effect)
    {
        ShaderError? validationError = Validate(effect);
        if (validationError is not null)
            return Result<byte[], ShaderError>.Fail(validationError);

        // ---- Object-table plan. Index 0 is reserved (never referenced); every object
        // index stored in a value blob must be < object_count.
        int nextObject = 1;

        // Texture parameters each own one object (their value blob is its index).
        var textureObjectIndex = new Dictionary<int, int>(); // param index -> object index
        for (int p = 0; p < effect.Parameters.Count; p++)
            if (IsTexture(effect.Parameters[p]))
                textureObjectIndex[p] = nextObject++;

        // Each sampler Texture-state owns a distinct object that later receives the
        // texture parameter's NAME via a usage=1 large record (the "sampler map").
        var samplerTextureObjectIndex = new Dictionary<(int Param, int State), int>();
        for (int p = 0; p < effect.Parameters.Count; p++)
        {
            IReadOnlyList<Fx2SamplerState> states = effect.Parameters[p].SamplerStates;
            for (int s = 0; s < states.Count; s++)
                if (states[s].TextureParameterName is not null)
                    samplerTextureObjectIndex[(p, s)] = nextObject++;
        }

        // Each present shader stage of each pass owns one object.
        var shaderObjectIndex = new Dictionary<(int Tech, int Pass, ShaderStage Stage), int>();
        for (int t = 0; t < effect.Techniques.Count; t++)
        {
            IReadOnlyList<Fx2Pass> passes = effect.Techniques[t].Passes;
            for (int p = 0; p < passes.Count; p++)
            {
                if (passes[p].VertexShaderIndex >= 0)
                    shaderObjectIndex[(t, p, ShaderStage.Vertex)] = nextObject++;
                if (passes[p].PixelShaderIndex >= 0)
                    shaderObjectIndex[(t, p, ShaderStage.Pixel)] = nextObject++;
            }
        }

        int objectCount = nextObject;

        // ---- Data pool. The first dword is zeroed so pool offset 0 doubles as the
        // null string (absent name/semantic) — same trick fxc and vkd3d use.
        using var poolMs = new MemoryStream();
        using var pool = new BinaryWriter(poolMs, Encoding.ASCII, leaveOpen: true);
        pool.Write(0u);

        // Parameter typedefs + values.
        var paramTypedefOfs = new uint[effect.Parameters.Count];
        var paramValueOfs = new uint[effect.Parameters.Count];
        for (int p = 0; p < effect.Parameters.Count; p++)
        {
            Fx2Parameter param = effect.Parameters[p];
            uint nameOfs = AddString(pool, param.Name);
            uint semanticOfs = AddString(pool, param.Semantic);

            if (param.Class == ClassObject)
            {
                paramTypedefOfs[p] = AddDwords(pool,
                    (uint)param.Type, (uint)param.Class, nameOfs, semanticOfs, (uint)param.Elements);

                if (IsTexture(param))
                {
                    paramValueOfs[p] = AddDwords(pool, (uint)textureObjectIndex[p]);
                }
                else
                {
                    // Sampler: pool each state's inner typedef/value first, then the
                    // state list itself ({count} + 4 dwords per state).
                    var stateRecords = new List<(uint Op, uint TypedefOfs, uint ValueOfs)>();
                    for (int s = 0; s < param.SamplerStates.Count; s++)
                    {
                        Fx2SamplerState state = param.SamplerStates[s];
                        if (state.TextureParameterName is not null)
                        {
                            uint innerTypedef = AddDwords(pool,
                                TypeTexture, ClassObject, 0u, 0u, 0u);
                            uint innerValue = AddDwords(pool,
                                (uint)samplerTextureObjectIndex[(p, s)]);
                            stateRecords.Add(((uint)state.Operation, innerTypedef, innerValue));
                        }
                        else
                        {
                            bool isFloat = state.FloatValue.HasValue;
                            uint innerTypedef = AddDwords(pool,
                                (uint)(isFloat ? TypeFloat : TypeInt), ClassScalar,
                                0u, 0u, 0u, 1u, 1u);
                            uint bits = isFloat
                                ? BitConverter.SingleToUInt32Bits(state.FloatValue!.Value)
                                : (uint)state.IntValue!.Value;
                            uint innerValue = AddDwords(pool, bits);
                            stateRecords.Add(((uint)state.Operation, innerTypedef, innerValue));
                        }
                    }

                    uint blobOfs = (uint)poolMs.Position;
                    pool.Write((uint)stateRecords.Count);
                    foreach ((uint op, uint typedefOfs, uint valueOfs) in stateRecords)
                    {
                        pool.Write(op);
                        pool.Write(0u); // state index — ignored by MojoShader; 0 per fxc
                        pool.Write(typedefOfs);
                        pool.Write(valueOfs);
                    }
                    paramValueOfs[p] = blobOfs;
                }
            }
            else
            {
                // Numeric typedef: dword5/dword6 are read by MojoShader as columns-then-rows
                // for every numeric class. fxc agrees for scalars/vectors, and the writer
                // only allows square matrices (validated above), so the F1 reader/writer
                // dims-order conflict cannot bite.
                paramTypedefOfs[p] = AddDwords(pool,
                    (uint)param.Type, (uint)param.Class, nameOfs, semanticOfs,
                    (uint)param.Elements, (uint)param.Columns, (uint)param.Rows);

                int elements = Math.Max(1, param.Elements);
                int dwordCount = param.Rows * param.Columns * elements;
                uint blobOfs = (uint)poolMs.Position;
                for (int i = 0; i < dwordCount; i++)
                {
                    float value = param.DefaultValue is not null && i < param.DefaultValue.Count
                        ? param.DefaultValue[i]
                        : 0f;
                    // The value blob's dword encoding follows the parameter TYPE: floats as
                    // IEEE-754, ints as raw integer dwords, bools normalized to 0/1 — what
                    // MojoShader's valuesI/valuesB and FNA's SetValue(int)/GetValueInt32
                    // read back. (CTAB defaults arrive as floats even for int/bool globals,
                    // so this conversion is load-bearing, not cosmetic.)
                    switch (param.Type)
                    {
                        case TypeBool:
                            pool.Write(value != 0f ? 1u : 0u);
                            break;
                        case TypeInt:
                            pool.Write((int)Math.Clamp(
                                (long)Math.Round((double)value), int.MinValue, int.MaxValue));
                            break;
                        default:
                            pool.Write(value);
                            break;
                    }
                }
                paramValueOfs[p] = blobOfs;
            }
        }

        // Technique/pass names and per-pass state lists. State order within a pass:
        // render states (IR order), then VertexShader, then PixelShader — the large-object
        // back-references below depend on these state indices.
        var techNameOfs = new uint[effect.Techniques.Count];
        var passNameOfs = new uint[effect.Techniques.Count][];
        var passStates = new List<(uint Op, uint TypedefOfs, uint ValueOfs)>[effect.Techniques.Count][];
        var shaderStateIndex = new Dictionary<(int Tech, int Pass, ShaderStage Stage), int>();

        for (int t = 0; t < effect.Techniques.Count; t++)
        {
            Fx2Technique technique = effect.Techniques[t];
            techNameOfs[t] = AddString(pool, technique.Name);
            passNameOfs[t] = new uint[technique.Passes.Count];
            passStates[t] = new List<(uint, uint, uint)>[technique.Passes.Count];

            for (int p = 0; p < technique.Passes.Count; p++)
            {
                Fx2Pass pass = technique.Passes[p];
                passNameOfs[t][p] = AddString(pool, pass.Name);
                var states = new List<(uint, uint, uint)>();

                foreach (Fx2RenderState rs in pass.RenderStates)
                {
                    uint typedefOfs = AddDwords(pool,
                        (uint)(rs.IsFloat ? TypeFloat : TypeInt), ClassScalar,
                        0u, 0u, 0u, 1u, 1u);
                    uint valueOfs = AddDwords(pool, rs.Value);
                    states.Add(((uint)rs.Operation, typedefOfs, valueOfs));
                }

                if (pass.VertexShaderIndex >= 0)
                {
                    uint typedefOfs = AddDwords(pool, TypeVertexShader, ClassObject, 0u, 0u, 0u);
                    uint valueOfs = AddDwords(pool,
                        (uint)shaderObjectIndex[(t, p, ShaderStage.Vertex)]);
                    shaderStateIndex[(t, p, ShaderStage.Vertex)] = states.Count;
                    states.Add((StateVertexShader, typedefOfs, valueOfs));
                }

                if (pass.PixelShaderIndex >= 0)
                {
                    uint typedefOfs = AddDwords(pool, TypePixelShader, ClassObject, 0u, 0u, 0u);
                    uint valueOfs = AddDwords(pool,
                        (uint)shaderObjectIndex[(t, p, ShaderStage.Pixel)]);
                    shaderStateIndex[(t, p, ShaderStage.Pixel)] = states.Count;
                    states.Add((StatePixelShader, typedefOfs, valueOfs));
                }

                passStates[t][p] = states;
            }
        }

        pool.Flush();
        byte[] poolBytes = poolMs.ToArray(); // length is 4-aligned by construction

        // ---- Structured stream.
        using var streamMs = new MemoryStream();
        using var stream = new BinaryWriter(streamMs, Encoding.ASCII, leaveOpen: true);

        int totalPassCount = effect.Techniques.Sum(t => t.Passes.Count);

        stream.Write((uint)effect.Parameters.Count);
        stream.Write((uint)effect.Techniques.Count);
        // "Shader count" — read and discarded by MojoShader and FNA; fxc fidelity value
        // per Wine: one slot per pass plus per shader-typed parameter (we emit none).
        stream.Write((uint)totalPassCount);
        stream.Write((uint)objectCount);

        for (int p = 0; p < effect.Parameters.Count; p++)
        {
            stream.Write(paramTypedefOfs[p]);
            stream.Write(paramValueOfs[p]);
            stream.Write(0u); // flags — ignored
            stream.Write(0u); // annotation count — ShadowDusk emits no annotations
        }

        for (int t = 0; t < effect.Techniques.Count; t++)
        {
            stream.Write(techNameOfs[t]);
            stream.Write(0u); // annotation count
            stream.Write((uint)effect.Techniques[t].Passes.Count);

            for (int p = 0; p < effect.Techniques[t].Passes.Count; p++)
            {
                stream.Write(passNameOfs[t][p]);
                stream.Write(0u); // annotation count
                stream.Write((uint)passStates[t][p].Count);

                foreach ((uint op, uint typedefOfs, uint valueOfs) in passStates[t][p])
                {
                    stream.Write(op);
                    stream.Write(0u); // state index — ignored
                    stream.Write(typedefOfs);
                    stream.Write(valueOfs);
                }
            }
        }

        // ---- Object sections: smalls (zero-length texture initializers), then larges
        // (shader bytecode; sampler-map name records). MojoShader resolves each large
        // record's target object through the state it back-references, so the
        // (technique, pass/param, state) triples must match the records written above.
        stream.Write((uint)textureObjectIndex.Count);
        stream.Write((uint)(shaderObjectIndex.Count + samplerTextureObjectIndex.Count));

        foreach (int p in textureObjectIndex.Keys.OrderBy(i => i))
        {
            stream.Write((uint)textureObjectIndex[p]);
            stream.Write(0u); // zero-length data: the two header dwords only
        }

        for (int t = 0; t < effect.Techniques.Count; t++)
        {
            for (int p = 0; p < effect.Techniques[t].Passes.Count; p++)
            {
                Fx2Pass pass = effect.Techniques[t].Passes[p];
                foreach ((ShaderStage stage, int shaderIdx) in EnumeratePassShaders(pass))
                {
                    ReadOnlyMemory<byte> bytecode = effect.Shaders[shaderIdx].Bytecode;
                    stream.Write((uint)t);                                    // technique
                    stream.Write((uint)p);                                    // pass
                    stream.Write(0u);                                         // element — ignored
                    stream.Write((uint)shaderStateIndex[(t, p, stage)]);      // state index
                    stream.Write(0u);                                         // usage 0 = code blob
                    stream.Write((uint)bytecode.Length);
                    stream.Write(bytecode.Span);
                    WritePadding(stream, bytecode.Length);
                }
            }
        }

        foreach (((int p, int s), int _) in samplerTextureObjectIndex.OrderBy(kv => kv.Value))
        {
            string textureName = effect.Parameters[p].SamplerStates[s].TextureParameterName!;
            byte[] nameBytes = Encoding.ASCII.GetBytes(textureName);
            stream.Write(0xFFFFFFFFu);            // technique -1 = parameter sampler state
            stream.Write((uint)p);                // parameter index
            stream.Write(0xFFFFFFFFu);            // element — ignored; -1 per Wine for this path
            stream.Write((uint)s);                // index into the sampler's state list
            stream.Write(1u);                     // usage 1 = parameter-name reference
            stream.Write((uint)(nameBytes.Length + 1));
            stream.Write(nameBytes);
            stream.Write((byte)0);
            WritePadding(stream, nameBytes.Length + 1);
        }

        stream.Flush();
        byte[] streamBytes = streamMs.ToArray();

        // ---- Assemble.
        using var fileMs = new MemoryStream(8 + poolBytes.Length + streamBytes.Length);
        using var file = new BinaryWriter(fileMs, Encoding.ASCII, leaveOpen: true);
        file.Write(Fx2VersionToken);
        file.Write((uint)poolBytes.Length);
        file.Write(poolBytes);
        file.Write(streamBytes);
        file.Flush();

        return Result<byte[], ShaderError>.Ok(fileMs.ToArray());
    }

    // -------------------------------------------------------------------------
    // Validation — MojoShader does not bounds-check, so every invariant it relies
    // on must be enforced here (fail loudly, never emit a corrupting binary).
    // -------------------------------------------------------------------------

    private static ShaderError? Validate(Fx2EffectDesc effect)
    {
        if (effect.Techniques.Count == 0)
            return Error("effect has no techniques (MojoShader requires at least one)");

        var textureIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (int p = 0; p < effect.Parameters.Count; p++)
        {
            Fx2Parameter param = effect.Parameters[p];
            if (string.IsNullOrEmpty(param.Name))
                return Error($"parameter #{p} has an empty name");
            if (!seenNames.Add(param.Name))
                return Error($"duplicate parameter name '{param.Name}'");

            if (!System.Text.Ascii.IsValid(param.Name) ||
                (param.Semantic is not null && !System.Text.Ascii.IsValid(param.Semantic)))
            {
                // fx_2_0 strings are ASCII; a lossy re-encode would break MojoShader's
                // exact-strcmp CTAB→parameter binding (release-mode memory corruption).
                return Error($"parameter '{param.Name}' has a non-ASCII name or semantic");
            }

            if (param.Elements < 0)
                return Error($"parameter '{param.Name}' has a negative element count ({param.Elements})");

            if (param.Class is >= ClassScalar and <= ClassMatrixColumns)
            {
                if (param.Type is not (TypeBool or TypeInt or TypeFloat))
                    return Error($"numeric parameter '{param.Name}' has non-numeric type {param.Type}");
                if (param.Rows <= 0 || param.Columns <= 0)
                    return Error($"numeric parameter '{param.Name}' has invalid dimensions {param.Rows}x{param.Columns}");

                // D3D9 has 256 float registers; 65536 dwords is far beyond any real effect
                // and guards the int arithmetic in the value-blob emission.
                long blobDwords = (long)param.Rows * param.Columns * Math.Max(1, param.Elements);
                if (blobDwords > 65536)
                    return Error($"parameter '{param.Name}' value blob is implausibly large ({blobDwords} dwords)");
                if (param.Class is ClassMatrixRows or ClassMatrixColumns && param.Rows != param.Columns)
                    return Error(
                        $"parameter '{param.Name}' is a non-square matrix ({param.Rows}x{param.Columns}); " +
                        "MojoShader and fxc disagree on the fx_2_0 dims order for non-square matrices " +
                        "(docs/fx2-binary-format.md §15 F1) — use a square matrix");
                if (param.SamplerStates.Count > 0)
                    return Error($"numeric parameter '{param.Name}' must not carry sampler states");

                int expected = param.Rows * param.Columns * Math.Max(1, param.Elements);
                if (param.DefaultValue is not null && param.DefaultValue.Count != expected)
                    return Error(
                        $"parameter '{param.Name}' default value has {param.DefaultValue.Count} components, expected {expected}");
            }
            else if (param.Class == ClassObject)
            {
                if (param.Type is < 4 or > 16)
                    return Error($"object parameter '{param.Name}' has invalid type {param.Type}");
                if (param.Elements != 0)
                    return Error($"object parameter '{param.Name}' is an array — not supported");

                if (IsTexture(param))
                {
                    if (param.SamplerStates.Count > 0)
                        return Error($"texture parameter '{param.Name}' must not carry sampler states");
                    textureIndexByName[param.Name] = p;
                }
                else if (IsSampler(param))
                {
                    foreach (Fx2SamplerState state in param.SamplerStates)
                    {
                        if (FnaThrowingSamplerStates.Contains(state.Operation))
                            return Error(
                                $"sampler '{param.Name}' uses sampler state {state.Operation} " +
                                "(BorderColor/SRGBTexture/ElementIndex/DMapOffset), which FNA's " +
                                "runtime throws NotImplementedException on — remove it from the sampler_state block");
                        if (state.Operation is < SamplerStateTexture or > 177)
                            return Error($"sampler '{param.Name}' has out-of-range state op {state.Operation}");

                        int setCount = (state.TextureParameterName is not null ? 1 : 0)
                                     + (state.FloatValue.HasValue ? 1 : 0)
                                     + (state.IntValue.HasValue ? 1 : 0);
                        if (setCount != 1)
                            return Error($"sampler '{param.Name}' state {state.Operation} must carry exactly one value");
                        if (state.TextureParameterName is not null && state.Operation != SamplerStateTexture)
                            return Error($"sampler '{param.Name}' state {state.Operation} cannot reference a texture");
                        if (state.Operation == SamplerStateTexture && state.TextureParameterName is null)
                            return Error($"sampler '{param.Name}' Texture state must reference a texture parameter");

                        if (state.TextureParameterName is not null)
                        {
                            // FNA builds the sampler→texture map by scanning only the
                            // parameters converted so far: the texture MUST precede the sampler.
                            if (!textureIndexByName.ContainsKey(state.TextureParameterName))
                                return Error(
                                    $"sampler '{param.Name}' references texture '{state.TextureParameterName}', " +
                                    "which is not declared as an earlier texture parameter (FNA requires " +
                                    "textures to precede the samplers that use them)");
                        }
                    }
                }
                else
                {
                    return Error($"object parameter '{param.Name}' of type {param.Type} is not supported");
                }
            }
            else
            {
                return Error($"parameter '{param.Name}' has invalid class {param.Class}");
            }
        }

        for (int t = 0; t < effect.Techniques.Count; t++)
        {
            Fx2Technique technique = effect.Techniques[t];
            if (string.IsNullOrEmpty(technique.Name) || !System.Text.Ascii.IsValid(technique.Name))
                return Error($"technique #{t} has an empty or non-ASCII name");

            for (int p = 0; p < technique.Passes.Count; p++)
            {
                Fx2Pass pass = technique.Passes[p];
                if (string.IsNullOrEmpty(pass.Name) || !System.Text.Ascii.IsValid(pass.Name))
                    return Error($"technique '{technique.Name}' pass #{p} has an empty or non-ASCII name");

                foreach ((ShaderStage stage, int idx) in EnumeratePassShaders(pass))
                {
                    if (idx >= effect.Shaders.Count)
                        return Error($"pass '{pass.Name}' references shader #{idx}, but only {effect.Shaders.Count} exist");
                    if (effect.Shaders[idx].Stage != stage)
                        return Error($"pass '{pass.Name}' binds shader #{idx} as {stage}, but it is a {effect.Shaders[idx].Stage} shader");
                    if (effect.Shaders[idx].Bytecode.Length == 0)
                        return Error($"pass '{pass.Name}' {stage} shader #{idx} has empty bytecode");
                }

                foreach (Fx2RenderState rs in pass.RenderStates)
                {
                    if (!FnaHonoredRenderStates.Contains(rs.Operation))
                        return Error(
                            $"pass '{pass.Name}' sets render state {rs.Operation}, which FNA's runtime " +
                            "does not honor (it throws NotImplementedException at load) — set it from " +
                            "game code instead");
                }
            }
        }

        // MojoShader binds every CTAB constant of every embedded shader to an effect
        // parameter by exact strcmp — a miss is an assert in debug builds and memory
        // corruption in release FNA. The builder guarantees this by construction; this
        // write-time check guards the public writer API (and any future builder drift).
        for (int s = 0; s < effect.Shaders.Count; s++)
        {
            // Choke-point stage check: the version token's high word says what the blob
            // actually IS (0xFFFE vertex / 0xFFFF pixel); the Stage tag says what the
            // pass binds it as. A producer mistake here (e.g. compiling a pass's
            // VertexShader with a ps_* profile) ships a binary fxc would have rejected,
            // and that breaks only inside the consumer's FNA at load/draw.
            ReadOnlySpan<byte> bytecode = effect.Shaders[s].Bytecode.Span;
            if (bytecode.Length >= 4)
            {
                uint versionToken = BitConverter.ToUInt32(bytecode[..4]);
                uint kind = versionToken >> 16;
                if (kind is 0xFFFE or 0xFFFF)
                {
                    ShaderStage actual = kind == 0xFFFE ? ShaderStage.Vertex : ShaderStage.Pixel;
                    if (actual != effect.Shaders[s].Stage)
                        return Error(
                            $"shader #{s} is tagged {effect.Shaders[s].Stage} but its version token " +
                            $"(0x{versionToken:X8}) is a {actual} token stream — a pass binding it " +
                            "would break inside FNA at load");
                }
            }

            var ctabResult = Reflection.CtabReader.Read(effect.Shaders[s].Bytecode.Span, sourceFile: "");
            if (ctabResult.IsFailure)
                return Error(
                    $"shader #{s} carries no readable CTAB constant table — MojoShader binds effect " +
                    $"parameters via the CTAB, so a shader without one binds nothing ({ctabResult.Error.Message})");

            foreach (var constant in ctabResult.Value.Constants)
            {
                if (!seenNames.Contains(constant.Name))
                    return Error(
                        $"shader #{s} CTAB constant '{constant.Name}' has no matching effect " +
                        "parameter — MojoShader binds by exact name and a miss corrupts memory in FNA");
            }
        }

        return null;
    }

    private static IEnumerable<(ShaderStage Stage, int ShaderIndex)> EnumeratePassShaders(Fx2Pass pass)
    {
        if (pass.VertexShaderIndex >= 0)
            yield return (ShaderStage.Vertex, pass.VertexShaderIndex);
        if (pass.PixelShaderIndex >= 0)
            yield return (ShaderStage.Pixel, pass.PixelShaderIndex);
    }

    private static bool IsTexture(Fx2Parameter p) => p.Class == ClassObject && p.Type is >= 5 and <= 9;
    private static bool IsSampler(Fx2Parameter p) => p.Class == ClassObject && p.Type is >= 10 and <= 14;

    /// <summary>
    /// Pool string: u32 length (including NUL) + bytes + NUL, padded so the next pool item
    /// starts 4-byte aligned. Null/empty returns offset 0 — the zeroed first pool dword,
    /// which readers interpret as "no string".
    /// </summary>
    private static uint AddString(BinaryWriter pool, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        uint offset = (uint)pool.BaseStream.Position;
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        pool.Write((uint)(bytes.Length + 1));
        pool.Write(bytes);
        pool.Write((byte)0);
        WritePadding(pool, bytes.Length + 1);
        return offset;
    }

    private static uint AddDwords(BinaryWriter pool, params uint[] values)
    {
        uint offset = (uint)pool.BaseStream.Position;
        foreach (uint value in values)
            pool.Write(value);
        return offset;
    }

    /// <summary>Pads <paramref name="dataLength"/> bytes of just-written data to a 4-byte
    /// boundary — the <c>4·⌈len/4⌉</c> advance MojoShader's object readers use.</summary>
    private static void WritePadding(BinaryWriter writer, int dataLength)
    {
        for (int i = dataLength; i % 4 != 0; i++)
            writer.Write((byte)0);
    }

    private static ShaderError Error(string message) => new(
        File: "",
        Line: 0,
        Column: 0,
        Code: "SD0302",
        Message: "fx_2_0 effect validation failed: " + message);
}
