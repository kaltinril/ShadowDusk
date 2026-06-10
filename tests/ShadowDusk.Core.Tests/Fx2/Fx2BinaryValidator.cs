#nullable enable

// The MojoShader-rule structural validator for fx_2_0 effects binaries (rung 2 of the
// Phase 39 evidence ladder). Implemented INDEPENDENTLY of Fx2EffectWriter, strictly from
// docs/fx2-binary-format.md (MojoShader's parser is the spec) and the fxc golden fixtures
// in tests/fixtures/golden/FNA/ — it exists to cross-check the writer, so it must never
// mirror the writer's code.
//
// IMPORTANT: this file is source-linked into the integration test project. It may depend
// only on the BCL and ShadowDusk.Core — no xunit, no FluentAssertions.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using ShadowDusk.Core;
using ShadowDusk.Core.Reflection;

namespace ShadowDusk.Core.Tests.Fx2;

/// <summary>Thrown by <see cref="Fx2BinaryValidator.Parse"/> on any violation of the
/// fx_2_0 hard requirements (docs/fx2-binary-format.md §13) — i.e. anything that would
/// make MojoShader hard-fail, assert-abort, corrupt memory, or make FNA's runtime throw.</summary>
public sealed class Fx2ValidationException : Exception
{
    public Fx2ValidationException(string message) : base(message) { }
}

/// <summary>One sampler-state record from a sampler parameter's value blob. Exactly one of
/// <see cref="ObjectIndex"/> (op 164/Texture — the object that carries the texture-name
/// mapping) or <see cref="DwordValue"/> (every other op) is set.</summary>
public sealed record Fx2ParsedSamplerState(int Operation, int? ObjectIndex, uint? DwordValue);

/// <summary>One effect parameter. <see cref="Rows"/>/<see cref="Columns"/> are MojoShader's
/// view of typedef dwords 6/5 (numeric classes only; 0 for object classes).</summary>
public sealed record Fx2ParsedParameter(
    string Name,
    int Class,
    int Type,
    int Rows,
    int Columns,
    int Elements,
    IReadOnlyList<Fx2ParsedSamplerState> SamplerStates);

/// <summary>One pass state. Shader states (ops 146/147) carry <see cref="ObjectIndex"/>;
/// FNA-honored render states carry the raw <see cref="DwordValue"/>.</summary>
public sealed record Fx2ParsedState(int Operation, int? ObjectIndex, uint? DwordValue);

public sealed record Fx2ParsedPass(string Name, IReadOnlyList<Fx2ParsedState> States);

public sealed record Fx2ParsedTechnique(string Name, IReadOnlyList<Fx2ParsedPass> Passes);

/// <summary>One embedded SM1–3 shader blob, in object-record order (small section first).
/// <see cref="Stage"/> derives from the resolved object type (15 = pixel, 16 = vertex),
/// which the binding state op (147/146) established.</summary>
public sealed record Fx2ParsedShader(
    ShaderStage Stage,
    uint VersionToken,
    IReadOnlyList<string> CtabConstantNames,
    int BytecodeLength);

/// <summary>The structured model of a validated fx_2_0 binary.</summary>
public sealed class Fx2ParsedEffect
{
    public required IReadOnlyList<Fx2ParsedParameter> Parameters { get; init; }
    public required IReadOnlyList<Fx2ParsedTechnique> Techniques { get; init; }
    public required int ObjectCount { get; init; }
    public required IReadOnlyList<Fx2ParsedShader> Shaders { get; init; }

    /// <summary>sampler parameter name → texture parameter name, from the usage==1
    /// name-reference object records (the MojoShader "sampler map").</summary>
    public required IReadOnlyDictionary<string, string> SamplerTextureMap { get; init; }
}

/// <summary>
/// Parses a complete fx_2_0 binary exactly the way FNA's MojoShader does, enforcing every
/// hard requirement of docs/fx2-binary-format.md §13 — plus the bounds checks MojoShader
/// itself skips (pool offsets, string lengths, value-blob sizes), because for MojoShader a
/// bad offset is silent memory corruption rather than an error.
/// </summary>
public static class Fx2BinaryValidator
{
    /// <summary>Parses and validates <paramref name="fxb"/>; throws
    /// <see cref="Fx2ValidationException"/> with a precise message on any violation.</summary>
    public static Fx2ParsedEffect Parse(byte[] fxb)
    {
        ArgumentNullException.ThrowIfNull(fxb);
        return new Parser(fxb).Run();
    }

    private sealed class Parser
    {
        private const uint EffectVersionToken = 0xFEFF0901;
        private const uint XnaWrapperToken = 0xBCF00BCF;
        private const uint ShaderEndToken = 0x0000FFFF;
        private const int Base = 8; // every stored offset is relative to file offset 8

        // Symbol type/class values (§6.1; identical to D3DXPARAMETER_*).
        private const int ClassObject = 4;
        private const int ClassStruct = 5;
        private const int TypeString = 4;
        private const int TypeTextureFirst = 5;   // TEXTURE
        private const int TypeTextureLast = 9;    // TEXTURECUBE
        private const int TypeSamplerFirst = 10;  // SAMPLER
        private const int TypeSamplerLast = 14;   // SAMPLERCUBE
        private const int TypePixelShader = 15;
        private const int TypeVertexShader = 16;

        private const int OpVertexShader = 146;
        private const int OpPixelShader = 147;
        private const int SamplerOpTexture = 164; // 0xA4 — on-disk ops are 164-based

        // The render states FNA's Effect.cs applies; every other pass-state op makes the
        // FNA runtime throw NotImplementedException (§8.2 † set).
        private static readonly HashSet<int> FnaHonoredRenderStateOps = new()
        {
            0, 1, 3, 6, 7, 8, 9, 13,
            22, 23, 24, 25, 26, 27, 28, 29,
            67, 68, 73, 75, 78, 79,
            88, 89, 90, 91, 92, 93, 94, 95, 96,
            98, 99, 100, 101, 102,
        };

        // Sampler-state ops FNA's runtime throws on (BorderColor, SRGBTexture,
        // ElementIndex, DMapOffset).
        private static readonly HashSet<int> FnaThrowingSamplerOps = new() { 168, 175, 176, 177 };

        private readonly byte[] _f;
        private int _pos;        // structured-stream cursor (absolute file offset)
        private int _poolSize;
        private int _objectCount;

        // The virtual object table: entry types start VOID (0) and are established as
        // values are parsed (§9). For objects typed by a sampler's Texture state we also
        // remember the owning sampler parameter, so name-reference records can be checked
        // against FNA's texture-before-sampler ordering rule.
        private int[] _objectTypes = Array.Empty<int>();
        private int?[] _objectSamplerOwner = Array.Empty<int?>();

        private readonly List<Fx2ParsedParameter> _parameters = new();
        private readonly List<Fx2ParsedTechnique> _techniques = new();
        private readonly List<Fx2ParsedShader> _shaders = new();
        private readonly Dictionary<string, string> _samplerTextureMap = new(StringComparer.Ordinal);

        public Parser(byte[] fxb) => _f = fxb;

        public Fx2ParsedEffect Run()
        {
            ReadHeader();
            ReadCountsHeaderAndAllocate(out uint parameterCount, out uint techniqueCount);

            for (uint i = 0; i < parameterCount; i++)
                ReadParameter((int)i);
            for (uint i = 0; i < techniqueCount; i++)
                ReadTechnique((int)i);

            ReadObjectSections();

            if (_pos != _f.Length)
                throw new Fx2ValidationException(
                    $"{_f.Length - _pos} trailing byte(s) after the last object record (offset 0x{_pos:X})");

            return new Fx2ParsedEffect
            {
                Parameters = _parameters,
                Techniques = _techniques,
                ObjectCount = _objectCount,
                Shaders = _shaders,
                SamplerTextureMap = _samplerTextureMap,
            };
        }

        // ---------------------------------------------------------------------
        // Header + counts (§2.1, §4)
        // ---------------------------------------------------------------------

        private void ReadHeader()
        {
            if (_f.Length < 8)
                throw new Fx2ValidationException(
                    $"file is {_f.Length} byte(s) — shorter than the 8-byte header");

            uint token = FileU32(0);
            if (token == XnaWrapperToken)
                throw new Fx2ValidationException(
                    "XNA4 wrapper token 0xBCF00BCF — ShadowDusk never emits the wrapper; expected a bare 0xFEFF0901 effect");
            if (token != EffectVersionToken)
                throw new Fx2ValidationException(
                    $"not an Effects Framework binary: version token 0x{token:X8}, expected 0xFEFF0901");

            uint poolSize = FileU32(4);
            if (poolSize > (uint)(_f.Length - 8))
                throw new Fx2ValidationException(
                    $"pool_size 0x{poolSize:X} exceeds the {_f.Length - 8} bytes after the header");
            if (poolSize % 4 != 0)
                throw new Fx2ValidationException(
                    $"pool_size 0x{poolSize:X} is not a multiple of 4 (fxc/vkd3d always align the pool)");

            _poolSize = (int)poolSize;
            _pos = Base + _poolSize;
        }

        private void ReadCountsHeaderAndAllocate(out uint parameterCount, out uint techniqueCount)
        {
            if (_f.Length - _pos < 16)
                throw new Fx2ValidationException(
                    $"fewer than 16 bytes remain at the counts header (offset 0x{_pos:X})");

            parameterCount = StreamU32("parameter_count");
            techniqueCount = StreamU32("technique_count");
            _ = StreamU32("counts-header dword 2"); // shader count — read and ignored (§4)
            uint objectCount = StreamU32("object_count");

            if (techniqueCount == 0)
                throw new Fx2ValidationException(
                    "technique_count is 0 — MojoShader takes techniques[0] unconditionally; at least one technique is required");
            if (objectCount > (uint)_f.Length)
                throw new Fx2ValidationException(
                    $"implausible object_count {objectCount} for a {_f.Length}-byte file");

            _objectCount = (int)objectCount;
            _objectTypes = new int[_objectCount];
            _objectSamplerOwner = new int?[_objectCount];
        }

        // ---------------------------------------------------------------------
        // Parameters (§5) and annotations
        // ---------------------------------------------------------------------

        private void ReadParameter(int index)
        {
            string ctx = $"parameter #{index}";
            uint typedefOffset = StreamU32($"{ctx} typedef offset");
            uint valueOffset = StreamU32($"{ctx} value offset");
            _ = StreamU32($"{ctx} flags"); // ignored by MojoShader
            uint annotationCount = StreamU32($"{ctx} annotation count");
            ReadAnnotations(annotationCount, ctx);

            TypeDef td = ReadTypeDef(typedefOffset, ctx);
            if (td.Name is null)
                throw new Fx2ValidationException(
                    $"{ctx} has no name — CTAB constants bind to parameters by exact name match");

            ParsedValue value = ParseValue(td, valueOffset, samplerOwnerParam: index, ctx + $" ('{td.Name}')");

            _parameters.Add(new Fx2ParsedParameter(
                td.Name, td.Class, td.Type, td.Rows, td.Columns, td.Elements,
                value.SamplerStates ?? Array.Empty<Fx2ParsedSamplerState>()));
        }

        private void ReadAnnotations(uint count, string owner)
        {
            // Annotations are {typedef_offset, value_offset} pairs parsed with the same
            // readvalue machinery as parameters (§5); their contents are never interpreted,
            // but the pool offsets must still be valid.
            for (uint i = 0; i < count; i++)
            {
                string ctx = $"{owner} annotation #{i}";
                uint typedefOffset = StreamU32($"{ctx} typedef offset");
                uint valueOffset = StreamU32($"{ctx} value offset");
                TypeDef td = ReadTypeDef(typedefOffset, ctx);
                _ = ParseValue(td, valueOffset, samplerOwnerParam: null, ctx);
            }
        }

        // ---------------------------------------------------------------------
        // Typedefs (§6)
        // ---------------------------------------------------------------------

        /// <summary>Typedef as MojoShader reads it: dword5 = columns, dword6 = rows for all
        /// numeric classes (§6 — note the SCALAR/MATRIX swap caveat vs fxc's writers).</summary>
        private readonly record struct TypeDef(
            int Type, int Class, string? Name, int Elements, int Columns, int Rows, long StructValueBytes);

        private TypeDef ReadTypeDef(uint offset, string ctx)
        {
            int type = (int)PoolU32(offset, $"{ctx} typedef type");
            int cls = (int)PoolU32(offset + 4, $"{ctx} typedef class");
            uint nameOffset = PoolU32(offset + 8, $"{ctx} typedef name offset");
            uint semanticOffset = PoolU32(offset + 12, $"{ctx} typedef semantic offset");
            uint elements = PoolU32(offset + 16, $"{ctx} typedef element count");

            if (cls is < 0 or > 5)
                throw new Fx2ValidationException(
                    $"{ctx}: typedef class {cls} out of range 0–5 (MojoShader asserts)");

            string? name = ReadPoolString(nameOffset, $"{ctx} typedef name");
            _ = ReadPoolString(semanticOffset, $"{ctx} typedef semantic"); // validated, not modeled

            if (elements > (uint)_f.Length)
                throw new Fx2ValidationException(
                    $"{ctx}: implausible element count {elements}");

            switch (cls)
            {
                case <= 3: // SCALAR / VECTOR / MATRIX_ROWS / MATRIX_COLUMNS
                {
                    if (type is < 1 or > 3)
                        throw new Fx2ValidationException(
                            $"{ctx}: numeric typedef (class {cls}) requires type 1–3 (bool/int/float), got {type}");
                    int columns = (int)PoolU32(offset + 20, $"{ctx} typedef column count");
                    int rows = (int)PoolU32(offset + 24, $"{ctx} typedef row count");
                    if (columns < 1 || rows < 1)
                        throw new Fx2ValidationException(
                            $"{ctx}: numeric typedef has degenerate dimensions {rows}x{columns}");
                    return new TypeDef(type, cls, name, (int)elements, columns, rows, 0);
                }
                case ClassObject:
                {
                    if (type is < 4 or > 16)
                        throw new Fx2ValidationException(
                            $"{ctx}: object typedef requires type 4–16 (STRING…VERTEXSHADER), got {type}");
                    return new TypeDef(type, cls, name, (int)elements, 0, 0, 0);
                }
                default: // ClassStruct
                {
                    uint memberCount = PoolU32(offset + 20, $"{ctx} struct member count");
                    long dwordsPerElement = 0;
                    for (uint m = 0; m < memberCount; m++)
                    {
                        // Members are 7 dwords: type, class, name, semantic (read,
                        // discarded), element_count, column_count, row_count (§6).
                        long rec = offset + 24 + (long)m * 28;
                        string mctx = $"{ctx} struct member #{m}";
                        int mtype = (int)PoolU32(rec, $"{mctx} type");
                        int mcls = (int)PoolU32(rec + 4, $"{mctx} class");
                        uint mname = PoolU32(rec + 8, $"{mctx} name offset");
                        uint msemantic = PoolU32(rec + 12, $"{mctx} semantic offset");
                        uint melements = PoolU32(rec + 16, $"{mctx} element count");
                        uint mcolumns = PoolU32(rec + 20, $"{mctx} column count");
                        uint mrows = PoolU32(rec + 24, $"{mctx} row count");

                        if (mcls is < 0 or > 3)
                            throw new Fx2ValidationException(
                                $"{mctx}: struct member class must be 0–3 (no nested structs), got {mcls}");
                        if (mtype is < 1 or > 3)
                            throw new Fx2ValidationException(
                                $"{mctx}: struct member type must be 1–3 (bool/int/float), got {mtype}");
                        _ = ReadPoolString(mname, $"{mctx} name");
                        _ = ReadPoolString(msemantic, $"{mctx} semantic");

                        dwordsPerElement += (long)mcolumns * mrows * Math.Max(1u, melements);
                    }
                    long structBytes = 4L * dwordsPerElement * Math.Max(1, (int)elements);
                    return new TypeDef(type, cls, name, (int)elements, 0, 0, structBytes);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Value blobs (§7)
        // ---------------------------------------------------------------------

        private sealed record ParsedValue(
            IReadOnlyList<Fx2ParsedSamplerState>? SamplerStates,
            IReadOnlyList<int>? ObjectIndices,
            uint? FirstDword);

        private ParsedValue ParseValue(TypeDef td, uint valueOffset, int? samplerOwnerParam, string ctx)
        {
            if (td.Class <= 3) // numeric: R×E file rows of C dwords each (§7.1)
            {
                long size = 4L * td.Columns * td.Rows * Math.Max(1, td.Elements);
                CheckPoolRange(valueOffset, size, $"{ctx} numeric value blob");
                return new ParsedValue(null, null, PoolU32(valueOffset, ctx));
            }

            if (td.Class == ClassStruct) // §7.4 — emitted zero-filled, sized, never interpreted
            {
                CheckPoolRange(valueOffset, td.StructValueBytes, $"{ctx} struct value blob");
                return new ParsedValue(null, null, null);
            }

            // OBJECT class.
            if (td.Type is >= TypeSamplerFirst and <= TypeSamplerLast)
                return new ParsedValue(
                    ReadSamplerStates(valueOffset, td.Type, samplerOwnerParam, ctx), null, null);

            // Non-sampler object: max(1, elements) dwords, each an object-table index;
            // parsing establishes the object's type (§7.2).
            int count = Math.Max(1, td.Elements);
            CheckPoolRange(valueOffset, 4L * count, $"{ctx} object value blob");
            var indices = new int[count];
            for (int k = 0; k < count; k++)
            {
                uint idx = PoolU32(valueOffset + (uint)(4 * k), $"{ctx} object index #{k}");
                ValidateObjectIndex(idx, $"{ctx} object value");
                _objectTypes[idx] = td.Type;
                indices[k] = (int)idx;
            }
            return new ParsedValue(null, indices, null);
        }

        private List<Fx2ParsedSamplerState> ReadSamplerStates(
            uint valueOffset, int samplerType, int? samplerOwnerParam, string ctx)
        {
            uint stateCount = PoolU32(valueOffset, $"{ctx} sampler state count");
            CheckPoolRange(valueOffset, 4 + 16L * stateCount, $"{ctx} sampler-state records");

            var states = new List<Fx2ParsedSamplerState>((int)stateCount);
            for (uint s = 0; s < stateCount; s++)
            {
                long rec = valueOffset + 4 + 16L * s;
                string sctx = $"{ctx} sampler state #{s}";

                uint op = PoolU32(rec, $"{sctx} op");
                _ = PoolU32(rec + 4, $"{sctx} index"); // ignored (fxc writes 0x100 here)
                uint stateTypedef = PoolU32(rec + 8, $"{sctx} typedef offset");
                uint stateValue = PoolU32(rec + 12, $"{sctx} value offset");

                if (op is < 164 or > 177)
                    throw new Fx2ValidationException(
                        $"{sctx}: op {op} is not 164-based (0xA4 Texture … 0xB1 DMapOffset)");
                if (FnaThrowingSamplerOps.Contains((int)op))
                    throw new Fx2ValidationException(
                        $"{sctx}: op {op} (BorderColor/SRGBTexture/ElementIndex/DMapOffset) makes FNA throw NotImplementedException");

                TypeDef std = ReadTypeDef(stateTypedef, sctx);

                if (op == SamplerOpTexture)
                {
                    // §7.3: typedef must be an OBJECT texture type; the value is one object
                    // index whose type MojoShader then overwrites with the sampler's type.
                    if (std.Class != ClassObject ||
                        std.Type is < TypeTextureFirst or > TypeTextureLast)
                        throw new Fx2ValidationException(
                            $"{sctx}: Texture state typedef must be OBJECT class 4 with type 5–9, got class {std.Class} type {std.Type}");

                    ParsedValue v = ParseValue(std, stateValue, samplerOwnerParam, sctx);
                    int objectIndex = v.ObjectIndices![0];
                    _objectTypes[objectIndex] = samplerType; // SAMP_TEXTURE post-step (§7.3)
                    _objectSamplerOwner[objectIndex] = samplerOwnerParam;
                    states.Add(new Fx2ParsedSamplerState((int)op, objectIndex, null));
                }
                else
                {
                    // Plain state: numeric typedef, exactly one dword of value. The golden
                    // shows fxc uses class 2 (not SCALAR) with C=R=1 — any numeric class
                    // 0–3 satisfies MojoShader's assert.
                    if (std.Class > 3)
                        throw new Fx2ValidationException(
                            $"{sctx}: value typedef must be numeric (class 0–3), got class {std.Class}");
                    if ((long)std.Columns * std.Rows * Math.Max(1, std.Elements) != 1)
                        throw new Fx2ValidationException(
                            $"{sctx}: value must be a single dword, got {std.Rows}x{std.Columns} x{Math.Max(1, std.Elements)} element(s)");

                    ParsedValue v = ParseValue(std, stateValue, null, sctx);
                    states.Add(new Fx2ParsedSamplerState((int)op, null, v.FirstDword));
                }
            }
            return states;
        }

        // ---------------------------------------------------------------------
        // Techniques, passes, states (§8)
        // ---------------------------------------------------------------------

        private void ReadTechnique(int index)
        {
            string ctx = $"technique #{index}";
            uint nameOffset = StreamU32($"{ctx} name offset");
            uint annotationCount = StreamU32($"{ctx} annotation count");
            ReadAnnotations(annotationCount, ctx);
            uint passCount = StreamU32($"{ctx} pass count");

            string name = ReadPoolString(nameOffset, $"{ctx} name") ?? string.Empty;
            var passes = new List<Fx2ParsedPass>((int)Math.Min(passCount, 1024));
            for (uint p = 0; p < passCount; p++)
                passes.Add(ReadPass($"{ctx} pass #{p}"));

            _techniques.Add(new Fx2ParsedTechnique(name, passes));
        }

        private Fx2ParsedPass ReadPass(string ctx)
        {
            uint nameOffset = StreamU32($"{ctx} name offset");
            uint annotationCount = StreamU32($"{ctx} annotation count");
            ReadAnnotations(annotationCount, ctx);
            uint stateCount = StreamU32($"{ctx} state count");

            string name = ReadPoolString(nameOffset, $"{ctx} name") ?? string.Empty;
            var states = new List<Fx2ParsedState>((int)Math.Min(stateCount, 1024));
            for (uint s = 0; s < stateCount; s++)
                states.Add(ReadPassState($"{ctx} state #{s}"));

            return new Fx2ParsedPass(name, states);
        }

        private Fx2ParsedState ReadPassState(string ctx)
        {
            uint op = StreamU32($"{ctx} operation");
            _ = StreamU32($"{ctx} index"); // ignored by MojoShader (§8.1)
            uint typedefOffset = StreamU32($"{ctx} typedef offset");
            uint valueOffset = StreamU32($"{ctx} value offset");

            TypeDef td = ReadTypeDef(typedefOffset, ctx);

            if (op is OpVertexShader or OpPixelShader)
            {
                int requiredType = op == OpVertexShader ? TypeVertexShader : TypePixelShader;
                if (td.Class != ClassObject || td.Type != requiredType)
                    throw new Fx2ValidationException(
                        $"{ctx}: shader state op {op} requires an OBJECT typedef of type {requiredType}, got class {td.Class} type {td.Type}");

                ParsedValue v = ParseValue(td, valueOffset, null, ctx);
                return new Fx2ParsedState((int)op, v.ObjectIndices![0], null);
            }

            if (!FnaHonoredRenderStateOps.Contains((int)op))
                throw new Fx2ValidationException(
                    $"{ctx}: pass state op {op} is neither a shader state (146/147) nor in FNA's honored render-state set — FNA throws NotImplementedException on it");

            if (td.Class > 3)
                throw new Fx2ValidationException(
                    $"{ctx}: render-state value typedef must be numeric (class 0–3), got class {td.Class}");
            if ((long)td.Columns * td.Rows * Math.Max(1, td.Elements) != 1)
                throw new Fx2ValidationException(
                    $"{ctx}: render-state value must be a single dword, got {td.Rows}x{td.Columns} x{Math.Max(1, td.Elements)} element(s)");

            ParsedValue value = ParseValue(td, valueOffset, null, ctx);
            return new Fx2ParsedState((int)op, null, value.FirstDword);
        }

        // ---------------------------------------------------------------------
        // Object sections (§9)
        // ---------------------------------------------------------------------

        private void ReadObjectSections()
        {
            if (_f.Length - _pos < 8)
                throw new Fx2ValidationException(
                    $"fewer than 8 bytes remain at the object-section counts (offset 0x{_pos:X})");

            uint smallCount = StreamU32("small_object_count");
            uint largeCount = StreamU32("large_object_count");

            for (uint i = 0; i < smallCount; i++)
                ReadSmallObject((int)i);
            for (uint i = 0; i < largeCount; i++)
                ReadLargeObject((int)i);
        }

        private void ReadSmallObject(int index)
        {
            string ctx = $"small-object record #{index}";
            uint objectIndex = StreamU32($"{ctx} object index");
            uint length = StreamU32($"{ctx} length");

            ValidateObjectIndex(objectIndex, ctx);
            int type = _objectTypes[objectIndex];
            if (type == 0)
                throw new Fx2ValidationException(
                    $"{ctx}: object {objectIndex} has no established type (VOID) — MojoShader assert-aborts on small records for unreferenced objects");

            ReadOnlySpan<byte> data = TakeObjectData(length, ctx);

            switch (type)
            {
                case TypeString:
                    if (length == 0 || data[(int)(length - 1)] != 0)
                        throw new Fx2ValidationException(
                            $"{ctx}: STRING object data must be a NUL-terminated string including the NUL");
                    break;

                case >= TypeTextureFirst and <= TypeSamplerLast:
                    // Optional mapped-parameter name; length 0 is the normal case for plain
                    // texture objects (fxc emits exactly that).
                    if (length > 0)
                        HandleNameMapping(objectIndex, data, ctx);
                    break;

                case TypePixelShader or TypeVertexShader:
                    ValidateShaderBlob(data, type, ctx);
                    break;

                default:
                    throw new Fx2ValidationException(
                        $"{ctx}: object type {type} is not valid in the small-object section (MojoShader assert-aborts)");
            }
        }

        private void ReadLargeObject(int index)
        {
            string ctx = $"large-object record #{index}";
            uint technique = StreamU32($"{ctx} technique");
            uint recordIndex = StreamU32($"{ctx} index");
            _ = StreamU32($"{ctx} element_index"); // ignored (fxc writes 0xFFFFFFFF or 0)
            uint stateIndex = StreamU32($"{ctx} state_index");
            uint usage = StreamU32($"{ctx} usage");
            uint length = StreamU32($"{ctx} length");

            // §9.2: the record carries no object index — it is resolved THROUGH the state
            // the record points at; that state must exist and carry an object-index value.
            int objectIndex;
            if (technique == 0xFFFFFFFF)
            {
                if (recordIndex >= (uint)_parameters.Count)
                    throw new Fx2ValidationException(
                        $"{ctx}: parameter index {recordIndex} out of range ({_parameters.Count} parameters)");
                Fx2ParsedParameter param = _parameters[(int)recordIndex];
                if (param.Class != ClassObject ||
                    param.Type is < TypeSamplerFirst or > TypeSamplerLast)
                    throw new Fx2ValidationException(
                        $"{ctx}: technique == -1 requires a sampler-typed parameter, but '{param.Name}' is class {param.Class} type {param.Type}");
                if (stateIndex >= (uint)param.SamplerStates.Count)
                    throw new Fx2ValidationException(
                        $"{ctx}: sampler-state index {stateIndex} out of range ('{param.Name}' has {param.SamplerStates.Count} states)");
                Fx2ParsedSamplerState state = param.SamplerStates[(int)stateIndex];
                if (state.ObjectIndex is null)
                    throw new Fx2ValidationException(
                        $"{ctx}: sampler state #{stateIndex} of '{param.Name}' (op {state.Operation}) does not carry an object index");
                objectIndex = state.ObjectIndex.Value;
            }
            else
            {
                if (technique >= (uint)_techniques.Count)
                    throw new Fx2ValidationException(
                        $"{ctx}: technique index {technique} out of range ({_techniques.Count} techniques)");
                Fx2ParsedTechnique tech = _techniques[(int)technique];
                if (recordIndex >= (uint)tech.Passes.Count)
                    throw new Fx2ValidationException(
                        $"{ctx}: pass index {recordIndex} out of range ('{tech.Name}' has {tech.Passes.Count} passes)");
                Fx2ParsedPass pass = tech.Passes[(int)recordIndex];
                if (stateIndex >= (uint)pass.States.Count)
                    throw new Fx2ValidationException(
                        $"{ctx}: state index {stateIndex} out of range (pass '{pass.Name}' has {pass.States.Count} states)");
                Fx2ParsedState state = pass.States[(int)stateIndex];
                if (state.ObjectIndex is null)
                    throw new Fx2ValidationException(
                        $"{ctx}: pass state #{stateIndex} (op {state.Operation}) does not carry an object index");
                objectIndex = state.ObjectIndex.Value;
            }

            int type = _objectTypes[objectIndex];
            if (type == 0)
                throw new Fx2ValidationException(
                    $"{ctx}: resolves to object {objectIndex} whose type was never established (VOID) — MojoShader silently drops the data");

            ReadOnlySpan<byte> data = TakeObjectData(length, ctx);

            switch (usage)
            {
                case 0: // raw blob — shader bytecode
                    if (type is not (TypePixelShader or TypeVertexShader))
                        throw new Fx2ValidationException(
                            $"{ctx}: usage 0 (raw blob) resolves to object type {type}; only shader objects (15/16) take bytecode");
                    ValidateShaderBlob(data, type, ctx);
                    break;

                case 1: // parameter-name reference (the sampler map)
                    if (type is < TypeTextureFirst or > TypeSamplerLast)
                        throw new Fx2ValidationException(
                            $"{ctx}: usage 1 (name reference) resolves to object type {type}; only texture/sampler objects (5–14) take a mapped name");
                    if (length == 0 || data[(int)(length - 1)] != 0)
                        throw new Fx2ValidationException(
                            $"{ctx}: usage 1 data must be a NUL-terminated parameter name");
                    HandleNameMapping((uint)objectIndex, data, ctx);
                    break;

                case 2:
                    throw new Fx2ValidationException(
                        $"{ctx}: usage 2 (standalone preshader) — ShadowDusk never emits preshaders");

                default:
                    throw new Fx2ValidationException($"{ctx}: unknown usage {usage}");
            }
        }

        /// <summary>Object data advances the stream by 4·⌈length/4⌉ bytes — 0 when length
        /// is 0 (§9.1); an unpadded advance would desynchronize the parser.</summary>
        private ReadOnlySpan<byte> TakeObjectData(uint length, string ctx)
        {
            long padded = length == 0 ? 0 : 4L * (((long)length + 3) / 4);
            if (_pos + padded > _f.Length)
                throw new Fx2ValidationException(
                    $"{ctx}: {length} data byte(s) (+pad) run past EOF (offset 0x{_pos:X}, file length 0x{_f.Length:X})");
            var data = new ReadOnlySpan<byte>(_f, _pos, (int)length);
            _pos += (int)padded;
            return data;
        }

        private void HandleNameMapping(uint objectIndex, ReadOnlySpan<byte> data, string ctx)
        {
            int nul = data.IndexOf((byte)0);
            if (nul < 0)
                throw new Fx2ValidationException($"{ctx}: mapped parameter name is not NUL-terminated");
            string textureName = Encoding.ASCII.GetString(data[..nul]);

            int textureIndex = _parameters.FindIndex(
                p => string.Equals(p.Name, textureName, StringComparison.Ordinal));
            if (textureIndex < 0)
                throw new Fx2ValidationException(
                    $"{ctx}: names parameter '{textureName}', which does not exist");
            Fx2ParsedParameter texture = _parameters[textureIndex];
            if (texture.Class != ClassObject ||
                texture.Type is < TypeTextureFirst or > TypeTextureLast)
                throw new Fx2ValidationException(
                    $"{ctx}: '{textureName}' is not a texture parameter (class {texture.Class} type {texture.Type})");

            int? samplerIndex = _objectSamplerOwner[objectIndex];
            if (samplerIndex is null)
                throw new Fx2ValidationException(
                    $"{ctx}: object {objectIndex} is not a sampler's Texture-state object — the name mapping has nothing to bind to");

            // FNA Effect.cs ordering rule: when FNA converts a sampler parameter it searches
            // only the parameters already converted, so the texture MUST come first.
            if (textureIndex >= samplerIndex.Value)
                throw new Fx2ValidationException(
                    $"{ctx}: texture parameter '{textureName}' (#{textureIndex}) must precede the sampler parameter '{_parameters[samplerIndex.Value].Name}' (#{samplerIndex.Value}) that references it (FNA builds the sampler map front-to-back)");

            _samplerTextureMap[_parameters[samplerIndex.Value].Name] = textureName;
        }

        // ---------------------------------------------------------------------
        // Embedded shader blobs (§10, §11)
        // ---------------------------------------------------------------------

        private void ValidateShaderBlob(ReadOnlySpan<byte> blob, int objectType, string ctx)
        {
            ShaderStage stage = objectType == TypePixelShader ? ShaderStage.Pixel : ShaderStage.Vertex;

            if (blob.Length < 8 || blob.Length % 4 != 0)
                throw new Fx2ValidationException(
                    $"{ctx}: shader blob of {blob.Length} byte(s) is too small or not dword-aligned");

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(blob);
            uint kind = version >> 16;
            if (kind != 0xFFFF && kind != 0xFFFE)
                throw new Fx2ValidationException(
                    $"{ctx}: not a D3D9 token stream (version token 0x{version:X8})");

            uint expectedKind = stage == ShaderStage.Pixel ? 0xFFFFu : 0xFFFEu;
            if (kind != expectedKind)
                throw new Fx2ValidationException(
                    $"{ctx}: stage mismatch — object typed {(stage == ShaderStage.Pixel ? "PIXELSHADER" : "VERTEXSHADER")} carries a {(kind == 0xFFFF ? "pixel" : "vertex")}-shader blob (0x{version:X8})");

            uint major = (version >> 8) & 0xFF;
            if (major is < 1 or > 3)
                throw new Fx2ValidationException(
                    $"{ctx}: shader version 0x{version:X8} is outside SM1.1–3.0");

            if (BinaryPrimitives.ReadUInt32LittleEndian(blob[^4..]) != ShaderEndToken)
                throw new Fx2ValidationException(
                    $"{ctx}: shader blob does not end with the 0x0000FFFF end token");

            // CTAB per §11 — CtabReader implements the same MojoShader rules (including the
            // CTAB-version == shader-version check).
            Result<CtabTable, ShaderError> ctab = CtabReader.Read(blob, "<embedded fx_2_0 shader>");
            if (ctab.IsFailure)
                throw new Fx2ValidationException($"{ctx}: embedded shader CTAB rejected — {ctab.Error.Message}");

            var names = new List<string>(ctab.Value.Constants.Count);
            foreach (CtabConstant constant in ctab.Value.Constants)
            {
                // §13.10: every CTAB constant must strcmp-match an effect parameter name;
                // a miss is release-mode memory corruption in MojoShader.
                if (!_parameters.Exists(
                        p => string.Equals(p.Name, constant.Name, StringComparison.Ordinal)))
                    throw new Fx2ValidationException(
                        $"{ctx}: CTAB constant '{constant.Name}' has no effect parameter with the identical name");
                names.Add(constant.Name);
            }

            _shaders.Add(new Fx2ParsedShader(stage, version, names, blob.Length));
        }

        // ---------------------------------------------------------------------
        // Primitive reads — every offset is bounds-checked (MojoShader checks none)
        // ---------------------------------------------------------------------

        private void ValidateObjectIndex(uint index, string ctx)
        {
            if (index == 0)
                throw new Fx2ValidationException($"{ctx}: object index 0 is reserved and must never be referenced");
            if (index >= (uint)_objectCount)
                throw new Fx2ValidationException(
                    $"{ctx}: object index {index} out of range (object_count {_objectCount})");
        }

        /// <summary>Reads a length-prefixed pool string (§3); null when the length is 0.</summary>
        private string? ReadPoolString(uint poolOffset, string ctx)
        {
            uint length = PoolU32(poolOffset, $"{ctx} string length");
            if (length == 0)
                return null;
            if ((long)poolOffset + 4 + length > _poolSize)
                throw new Fx2ValidationException(
                    $"{ctx}: string of {length} byte(s) at pool offset 0x{poolOffset:X} runs past the pool (size 0x{_poolSize:X})");
            if (_f[Base + poolOffset + 4 + length - 1] != 0)
                throw new Fx2ValidationException(
                    $"{ctx}: string at pool offset 0x{poolOffset:X} is not NUL-terminated (the length includes the NUL)");
            return Encoding.ASCII.GetString(_f, (int)(Base + poolOffset + 4), (int)length - 1);
        }

        private uint PoolU32(long poolOffset, string ctx)
        {
            if (poolOffset < 0 || poolOffset + 4 > _poolSize)
                throw new Fx2ValidationException(
                    $"{ctx}: pool offset 0x{poolOffset:X} out of bounds (pool size 0x{_poolSize:X})");
            return FileU32((int)(Base + poolOffset));
        }

        private void CheckPoolRange(uint poolOffset, long byteCount, string ctx)
        {
            if (byteCount < 0 || (long)poolOffset + byteCount > _poolSize)
                throw new Fx2ValidationException(
                    $"{ctx}: {byteCount} byte(s) at pool offset 0x{poolOffset:X} run past the pool (size 0x{_poolSize:X})");
        }

        private uint StreamU32(string what)
        {
            if (_pos + 4 > _f.Length)
                throw new Fx2ValidationException(
                    $"unexpected EOF reading {what} at offset 0x{_pos:X}");
            uint value = FileU32(_pos);
            _pos += 4;
            return value;
        }

        private uint FileU32(int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(_f.AsSpan(offset, 4));
    }
}
