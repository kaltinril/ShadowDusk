#nullable enable

namespace ShadowDusk.Core.Reflection.Spirv;

/// <summary>
/// Walks a parsed <see cref="SpirvModule"/> and reconstructs the reflection data the
/// <c>.mgfx</c> writer needs: constant-buffer layouts and resource (texture / sampler)
/// bindings. Designed to MATCH what the native DXIL oracle
/// (<c>DxilReflectionExtractor</c>) emits for the same shader compiled with DX layout.
///
/// <para>The SPIR-V is produced by DXC with <c>-fvk-use-dx-layout</c>, so member
/// byte offsets are taken verbatim from <c>Offset</c> decorations and member byte
/// sizes are computed with the same HLSL constant-buffer packing the oracle reports.</para>
/// </summary>
internal sealed class SpirvReflectionParser
{
    // ---- Type model ------------------------------------------------------------

    private abstract class SpirvType { }

    private sealed class ScalarType : SpirvType
    {
        public required EffectParameterType ElementType { get; init; }
        public required int                 ByteSize    { get; init; } // 4 for f32/i32/bool
    }

    private sealed class VectorType : SpirvType
    {
        public required ScalarType Component { get; init; }
        public required int        Count     { get; init; } // 2..4
    }

    private sealed class MatrixType : SpirvType
    {
        public required VectorType Column     { get; init; }
        public required int        ColumnCount { get; init; }
    }

    private sealed class ArrayType : SpirvType
    {
        public required SpirvType Element { get; init; }
        public required int       Length  { get; init; }      // 0 for runtime array
        public required int       Stride  { get; init; }      // ArrayStride decoration
    }

    private sealed class StructType : SpirvType
    {
        public required uint Id { get; init; }
    }

    private sealed class ImageType : SpirvType
    {
        public required TextureDimension Dimension { get; init; }
    }

    private sealed class SamplerType : SpirvType { }

    private sealed class SampledImageType : SpirvType
    {
        public required ImageType Image { get; init; }
    }

    private sealed class PointerType : SpirvType
    {
        public required uint              PointeeId    { get; init; }
        public required SpirvStorageClass StorageClass { get; init; }
    }

    // ---- Collected raw data ----------------------------------------------------

    private readonly Dictionary<uint, SpirvType> _types   = new();
    private readonly Dictionary<uint, int>       _intConst = new(); // OpConstant integer values
    private readonly Dictionary<uint, string>    _names    = new(); // result id -> OpName
    private readonly Dictionary<uint, Dictionary<int, string>> _memberNames = new();

    // Decorations keyed by target id.
    private readonly HashSet<uint>            _blockStructs = new();
    private readonly Dictionary<uint, int>    _binding      = new();
    private readonly Dictionary<uint, int>    _descriptorSet = new();
    private readonly Dictionary<uint, int>    _arrayStride  = new();

    // Member decorations: target struct id -> (member index -> value).
    private readonly Dictionary<uint, Dictionary<int, int>>  _memberOffset      = new();
    private readonly Dictionary<uint, Dictionary<int, int>>  _memberMatrixStride = new();
    private readonly Dictionary<uint, Dictionary<int, bool>> _memberColMajor    = new();

    // Struct member type ids, in declaration order, keyed by struct id.
    private readonly Dictionary<uint, uint[]> _structMembers = new();

    // OpVariable: result id -> (type id, storage class).
    private readonly List<(uint ResultId, uint TypeId, SpirvStorageClass Storage)> _variables = new();

    private readonly SpirvModule _module;

    public SpirvReflectionParser(SpirvModule module) => _module = module;

    // ---- Public entry point ----------------------------------------------------

    public (IReadOnlyList<ConstantBufferReflection> ConstantBuffers,
            IReadOnlyList<TextureReflection> Textures,
            IReadOnlyList<SamplerReflection> Samplers) Reflect()
    {
        CollectInstructions();

        // DXC's SPIR-V binding numbers are a FLAT auto-allocated namespace (with
        // -auto-binding-space N, every SRV / sampler / cbuffer gets a distinct,
        // sequentially-increasing Binding decoration). The DXIL oracle instead reports
        // the HLSL register WITHIN each resource class (textures t0,t1,…; samplers
        // s0,s1,…; cbuffers b0,b1,…) — each class numbered independently from 0.
        //
        // To reproduce the oracle's per-class register, we DON'T use the raw Binding
        // value. Instead we bucket each resource by class, sort each bucket by its raw
        // SPIR-V Binding (== HLSL declaration order), and assign 0-based slots within
        // the class. This recovers the same t#/s#/b# the oracle reports.
        var cbufferVars = new List<(uint Id, StructType Struct, int RawBinding)>();
        var textureVars = new List<(uint Id, ImageType Image, int RawBinding)>();
        var samplerVars = new List<(uint Id, int RawBinding)>();
        var combinedVars = new List<(uint Id, ImageType Image, int RawBinding)>();

        foreach (var (resultId, typeId, storage) in _variables)
        {
            // Only uniform/uniform-constant resources are reflected.
            if (storage is not (SpirvStorageClass.Uniform or SpirvStorageClass.UniformConstant))
                continue;

            // The variable's declared type is a pointer; follow it to the pointee.
            if (!_types.TryGetValue(typeId, out SpirvType? ptrType) || ptrType is not PointerType ptr)
                continue;

            if (!_types.TryGetValue(ptr.PointeeId, out SpirvType? pointee))
                continue;

            int rawBinding = _binding.TryGetValue(resultId, out int b) ? b : 0;

            switch (pointee)
            {
                case StructType st when _blockStructs.Contains(st.Id):
                    cbufferVars.Add((resultId, st, rawBinding));
                    break;
                case ImageType img:
                    textureVars.Add((resultId, img, rawBinding));
                    break;
                case SamplerType:
                    samplerVars.Add((resultId, rawBinding));
                    break;
                case SampledImageType si:
                    combinedVars.Add((resultId, si.Image, rawBinding));
                    break;
            }
        }

        var textures = new List<TextureReflection>();
        var samplers = new List<SamplerReflection>();

        // Separate-image textures get texture-class slots; separate samplers get
        // sampler-class slots. A combined sampled-image counts in BOTH classes.
        int textureSlot = 0;
        foreach (var (id, image, _) in textureVars.OrderBy(t => t.RawBinding))
            textures.Add(new TextureReflection
            {
                Name      = ResourceName(id),
                BindSlot  = textureSlot++,
                Dimension = image.Dimension,
            });

        int samplerSlot = 0;
        foreach (var (id, _) in samplerVars.OrderBy(s => s.RawBinding))
            samplers.Add(new SamplerReflection
            {
                Name     = ResourceName(id),
                BindSlot = samplerSlot++,
            });

        // Combined texture+sampler (Texture.Sample with a SamplerState merged into one
        // SPIR-V resource): surface in both classes, each with its own class slot.
        foreach (var (id, image, _) in combinedVars.OrderBy(c => c.RawBinding))
        {
            string name = ResourceName(id);
            textures.Add(new TextureReflection
            {
                Name      = name,
                BindSlot  = textureSlot++,
                Dimension = image.Dimension,
            });
            samplers.Add(new SamplerReflection
            {
                Name     = name,
                BindSlot = samplerSlot++,
            });
        }

        var constantBuffers = new List<ConstantBufferReflection>();
        int cbufferSlot = 0;
        foreach (var (id, st, _) in cbufferVars.OrderBy(c => c.RawBinding))
            constantBuffers.Add(BuildConstantBuffer(id, st, cbufferSlot++));

        return (constantBuffers, textures, samplers);
    }

    // ---- Constant buffer reconstruction ---------------------------------------

    private ConstantBufferReflection BuildConstantBuffer(uint variableId, StructType st, int bindSlot)
    {
        uint[] memberTypes = _structMembers.TryGetValue(st.Id, out uint[]? mt) ? mt : Array.Empty<uint>();
        _memberOffset.TryGetValue(st.Id, out Dictionary<int, int>? offsets);
        _memberNames.TryGetValue(st.Id, out Dictionary<int, string>? names);

        var variables = new List<VariableReflection>(memberTypes.Length);

        for (int i = 0; i < memberTypes.Length; i++)
        {
            SpirvType? memberType = _types.GetValueOrDefault(memberTypes[i]);
            int offset = offsets is not null && offsets.TryGetValue(i, out int o) ? o : 0;
            string memberName = names is not null && names.TryGetValue(i, out string? n) ? n : $"member{i}";

            variables.Add(BuildVariable(memberName, offset, memberType));
        }

        // The DXIL oracle reports the cbuffer's HLSL name. DXC names the cbuffer
        // variable (the OpVariable) after the HLSL identifier, while the struct type
        // gets a 'type.<Name>' OpName. Prefer the variable name.
        string cbName = CBufferName(variableId, st.Id);

        // Constant-buffer total size: the oracle reports cbDesc.Size, which HLSL rounds
        // up to a 16-byte boundary. Compute from the last member's offset + size.
        int sizeBytes = ComputeStructSize(memberTypes, offsets);

        return new ConstantBufferReflection
        {
            Name      = cbName,
            SizeBytes = sizeBytes,
            BindSlot  = bindSlot,
            Variables = variables,
        };
    }

    private VariableReflection BuildVariable(string name, int startOffset, SpirvType? type)
    {
        var (cls, ptype, rows, cols, elements, sizeBytes) = DescribeVariable(type);

        return new VariableReflection
        {
            Name           = name,
            StartOffset    = startOffset,
            SizeBytes      = sizeBytes,
            ParameterClass = cls,
            ParameterType  = ptype,
            Rows           = rows,
            Columns        = cols,
            Elements       = elements,
            Members        = BuildStructMembers(type),
        };
    }

    /// <summary>
    /// Phase 43, F10: recursively extracts struct member variables, mirroring the
    /// DXIL oracle's <c>ExtractStructMembers</c>: member <c>StartOffset</c> is the
    /// offset WITHIN the parent struct (the SPIR-V member Offset decoration),
    /// member <c>SizeBytes</c> is 0 (the oracle reports 0 for members — D3D's
    /// per-member type description carries no size), and nesting recurses. Arrays
    /// of structs report the element type's members, exactly as D3D does.
    /// </summary>
    private IReadOnlyList<VariableReflection>? BuildStructMembers(SpirvType? type)
    {
        if (type is ArrayType arr)
            return BuildStructMembers(arr.Element);
        if (type is not StructType st)
            return null;

        uint[] memberTypes = _structMembers.TryGetValue(st.Id, out uint[]? mt) ? mt : Array.Empty<uint>();
        if (memberTypes.Length == 0)
            return null;

        _memberOffset.TryGetValue(st.Id, out Dictionary<int, int>? offsets);
        _memberNames.TryGetValue(st.Id, out Dictionary<int, string>? names);

        var members = new List<VariableReflection>(memberTypes.Length);
        for (int i = 0; i < memberTypes.Length; i++)
        {
            SpirvType? memberType = _types.GetValueOrDefault(memberTypes[i]);
            var (cls, ptype, rows, cols, elements, _) = DescribeVariable(memberType);

            members.Add(new VariableReflection
            {
                Name           = names is not null && names.TryGetValue(i, out string? n) ? n : $"member{i}",
                StartOffset    = offsets is not null && offsets.TryGetValue(i, out int o) ? o : 0,
                SizeBytes      = 0, // the DXIL oracle reports 0 for struct members
                ParameterClass = cls,
                ParameterType  = ptype,
                Rows           = rows,
                Columns        = cols,
                Elements       = elements,
                Members        = BuildStructMembers(memberType),
            });
        }

        return members;
    }

    /// <summary>
    /// Maps a member type to the (class, type, rows, columns, elements, sizeBytes)
    /// tuple matching the DXIL oracle. Mirrors DxilReflectionExtractor exactly:
    /// <list type="bullet">
    ///   <item>scalar  -> Scalar, rows=1, cols=1, size=4</item>
    ///   <item>vectorN -> Vector, rows=1, cols=N, size=4N</item>
    ///   <item>matRxC  -> Matrix, rows=R, cols=C, size=4*R*C (DX-layout, fully packed)</item>
    ///   <item>array   -> elements=length; size rounded up to 16 (the oracle's
    ///         '(size+15)&amp;~15' for ElementCount>0).</item>
    /// </list>
    /// </summary>
    private (EffectParameterClass Class, EffectParameterType Type, int Rows, int Cols, int Elements, int Size)
        DescribeVariable(SpirvType? type)
    {
        switch (type)
        {
            case ScalarType s:
                return (EffectParameterClass.Scalar, s.ElementType, 1, 1, 0, s.ByteSize);

            case VectorType v:
                return (EffectParameterClass.Vector, v.Component.ElementType, 1, v.Count, 0,
                        v.Component.ByteSize * v.Count);

            case MatrixType m:
            {
                int rows = m.Column.Count;          // each column vector spans 'rows' components
                int cols = m.ColumnCount;
                int size = m.Column.Component.ByteSize * rows * cols;
                return (EffectParameterClass.Matrix, m.Column.Component.ElementType, rows, cols, 0, size);
            }

            case ArrayType a:
            {
                var (cls, ptype, rows, cols, _, _) = DescribeVariable(a.Element);
                // The oracle's array size = (totalBytes + 15) & ~15, where totalBytes
                // is the array stride * length (stride already padded to 16 by DX layout).
                int total = a.Stride > 0 ? a.Stride * a.Length : 0;
                int padded = (total + 15) & ~15;
                return (cls, ptype, rows, cols, a.Length, padded);
            }

            case StructType st:
            {
                // Phase 43, F10 — mirrors the DXIL oracle for struct members:
                // Class=Struct, Type=Void, Rows = 1, Columns = the TOTAL scalar
                // component count of all members (D3D's struct type description),
                // size = packed size (last member offset + last member size).
                return (EffectParameterClass.Struct, EffectParameterType.Void,
                        1, StructComponentCount(st), 0, StructSizeBytes(st));
            }

            default:
                return (EffectParameterClass.Scalar, EffectParameterType.Void, 1, 1, 0, 0);
        }
    }

    /// <summary>Packed struct size: last member's Offset decoration + its size.</summary>
    private int StructSizeBytes(StructType st)
    {
        uint[] memberTypes = _structMembers.TryGetValue(st.Id, out uint[]? mt) ? mt : Array.Empty<uint>();
        if (memberTypes.Length == 0)
            return 0;

        _memberOffset.TryGetValue(st.Id, out Dictionary<int, int>? offsets);
        int lastIndex = memberTypes.Length - 1;
        int lastOffset = offsets is not null && offsets.TryGetValue(lastIndex, out int o) ? o : 0;
        var (_, _, _, _, _, lastSize) = DescribeVariable(_types.GetValueOrDefault(memberTypes[lastIndex]));
        return lastOffset + lastSize;
    }

    /// <summary>
    /// Total scalar component count across all members (recursive) — what D3D
    /// reports as a struct type's ColumnCount.
    /// </summary>
    private int StructComponentCount(StructType st)
    {
        uint[] memberTypes = _structMembers.TryGetValue(st.Id, out uint[]? mt) ? mt : Array.Empty<uint>();
        int total = 0;
        foreach (uint typeId in memberTypes)
        {
            SpirvType? member = _types.GetValueOrDefault(typeId);
            total += member switch
            {
                ScalarType         => 1,
                VectorType v       => v.Count,
                MatrixType m       => m.Column.Count * m.ColumnCount,
                ArrayType a        => a.Length * ComponentCountOf(a.Element),
                StructType nested  => StructComponentCount(nested),
                _                  => 0,
            };
        }
        return total;
    }

    private int ComponentCountOf(SpirvType? type) => type switch
    {
        ScalarType        => 1,
        VectorType v      => v.Count,
        MatrixType m      => m.Column.Count * m.ColumnCount,
        ArrayType a       => a.Length * ComponentCountOf(a.Element),
        StructType nested => StructComponentCount(nested),
        _                 => 0,
    };

    private int ComputeStructSize(uint[] memberTypes, Dictionary<int, int>? offsets)
    {
        if (memberTypes.Length == 0)
            return 0;

        int lastIndex = memberTypes.Length - 1;
        int lastOffset = offsets is not null && offsets.TryGetValue(lastIndex, out int o) ? o : 0;
        SpirvType? lastType = _types.GetValueOrDefault(memberTypes[lastIndex]);
        var (_, _, _, _, _, lastSize) = DescribeVariable(lastType);

        int raw = lastOffset + lastSize;
        // HLSL constant buffers round the total up to a 16-byte boundary.
        return (raw + 15) & ~15;
    }

    // ---- Naming helpers --------------------------------------------------------

    private string CBufferName(uint variableId, uint structId)
    {
        // DXC names the cbuffer OpVariable after the HLSL cbuffer identifier; the
        // struct type carries 'type.<Name>' or 'type.ConstantBuffer.<Name>'.
        if (_names.TryGetValue(variableId, out string? vName) && !string.IsNullOrEmpty(vName))
            return vName;

        if (_names.TryGetValue(structId, out string? sName) && !string.IsNullOrEmpty(sName))
            return StripTypePrefix(sName);

        return "$Globals";
    }

    private static string StripTypePrefix(string typeName)
    {
        // 'type.ConstantBuffer.Foo' -> 'Foo'; 'type.Foo' -> 'Foo'.
        const string cb = "type.ConstantBuffer.";
        if (typeName.StartsWith(cb, StringComparison.Ordinal))
            return typeName.Substring(cb.Length);
        const string t = "type.";
        if (typeName.StartsWith(t, StringComparison.Ordinal))
            return typeName.Substring(t.Length);
        return typeName;
    }

    private string ResourceName(uint resultId) =>
        _names.TryGetValue(resultId, out string? n) && !string.IsNullOrEmpty(n) ? n : $"resource{resultId}";

    // ---- Instruction collection ------------------------------------------------

    private void CollectInstructions()
    {
        foreach (SpirvModule.Instruction instr in _module.Instructions)
        {
            uint[] ops = instr.Operands;
            switch (instr.Opcode)
            {
                case SpirvOpcode.OpName when ops.Length >= 2:
                    _names[ops[0]] = SpirvModule.DecodeString(ops, 1);
                    break;

                case SpirvOpcode.OpMemberName when ops.Length >= 3:
                {
                    uint structId = ops[0];
                    int memberIndex = (int)ops[1];
                    if (!_memberNames.TryGetValue(structId, out var dict))
                        _memberNames[structId] = dict = new Dictionary<int, string>();
                    dict[memberIndex] = SpirvModule.DecodeString(ops, 2);
                    break;
                }

                case SpirvOpcode.OpDecorate when ops.Length >= 2:
                    CollectDecorate(ops);
                    break;

                case SpirvOpcode.OpMemberDecorate when ops.Length >= 3:
                    CollectMemberDecorate(ops);
                    break;

                case SpirvOpcode.OpConstant when ops.Length >= 3:
                    // ops: [resultType, resultId, value...]. Record the integer value.
                    _intConst[ops[1]] = (int)ops[2];
                    break;

                case SpirvOpcode.OpTypeVoid when ops.Length >= 1:
                    _types[ops[0]] = new ScalarType { ElementType = EffectParameterType.Void, ByteSize = 0 };
                    break;

                case SpirvOpcode.OpTypeBool when ops.Length >= 1:
                    _types[ops[0]] = new ScalarType { ElementType = EffectParameterType.Bool, ByteSize = 4 };
                    break;

                case SpirvOpcode.OpTypeInt when ops.Length >= 2:
                    _types[ops[0]] = new ScalarType { ElementType = EffectParameterType.Int32, ByteSize = 4 };
                    break;

                case SpirvOpcode.OpTypeFloat when ops.Length >= 2:
                    _types[ops[0]] = new ScalarType { ElementType = EffectParameterType.Single, ByteSize = 4 };
                    break;

                case SpirvOpcode.OpTypeVector when ops.Length >= 3:
                    CollectVector(ops);
                    break;

                case SpirvOpcode.OpTypeMatrix when ops.Length >= 3:
                    CollectMatrix(ops);
                    break;

                case SpirvOpcode.OpTypeArray when ops.Length >= 3:
                    CollectArray(ops);
                    break;

                case SpirvOpcode.OpTypeRuntimeArray when ops.Length >= 2:
                    _types[ops[0]] = new ArrayType
                    {
                        Element = _types.GetValueOrDefault(ops[1]) ?? new ScalarType
                        {
                            ElementType = EffectParameterType.Void, ByteSize = 0,
                        },
                        Length  = 0,
                        Stride  = _arrayStride.TryGetValue(ops[0], out int rs) ? rs : 0,
                    };
                    break;

                case SpirvOpcode.OpTypeStruct when ops.Length >= 1:
                {
                    uint structId = ops[0];
                    var members = new uint[ops.Length - 1];
                    Array.Copy(ops, 1, members, 0, members.Length);
                    _structMembers[structId] = members;
                    _types[structId] = new StructType { Id = structId };
                    break;
                }

                case SpirvOpcode.OpTypePointer when ops.Length >= 3:
                    _types[ops[0]] = new PointerType
                    {
                        StorageClass = (SpirvStorageClass)ops[1],
                        PointeeId    = ops[2],
                    };
                    break;

                case SpirvOpcode.OpTypeImage when ops.Length >= 3:
                    _types[ops[0]] = new ImageType { Dimension = MapDim((SpirvDim)ops[2]) };
                    break;

                case SpirvOpcode.OpTypeSampler when ops.Length >= 1:
                    _types[ops[0]] = new SamplerType();
                    break;

                case SpirvOpcode.OpTypeSampledImage when ops.Length >= 2:
                {
                    SpirvType? imgType = _types.GetValueOrDefault(ops[1]);
                    _types[ops[0]] = new SampledImageType
                    {
                        Image = imgType as ImageType ?? new ImageType { Dimension = TextureDimension.Unknown },
                    };
                    break;
                }

                case SpirvOpcode.OpVariable when ops.Length >= 3:
                    // ops: [resultType, resultId, storageClass, (initializer)].
                    _variables.Add((ops[1], ops[0], (SpirvStorageClass)ops[2]));
                    break;
            }
        }
    }

    private void CollectVector(uint[] ops)
    {
        SpirvType? comp = _types.GetValueOrDefault(ops[1]);
        if (comp is ScalarType s)
            _types[ops[0]] = new VectorType { Component = s, Count = (int)ops[2] };
    }

    private void CollectMatrix(uint[] ops)
    {
        // OpTypeMatrix: [resultId, columnTypeId, columnCount]. Column type is a vector.
        SpirvType? col = _types.GetValueOrDefault(ops[1]);
        if (col is VectorType v)
            _types[ops[0]] = new MatrixType { Column = v, ColumnCount = (int)ops[2] };
    }

    private void CollectArray(uint[] ops)
    {
        // OpTypeArray: [resultId, elementTypeId, lengthConstId].
        SpirvType element = _types.GetValueOrDefault(ops[1]) ?? new ScalarType
        {
            ElementType = EffectParameterType.Void, ByteSize = 0,
        };
        int length = _intConst.TryGetValue(ops[2], out int len) ? len : 0;
        int stride = _arrayStride.TryGetValue(ops[0], out int s) ? s : 0;
        _types[ops[0]] = new ArrayType { Element = element, Length = length, Stride = stride };
    }

    private void CollectDecorate(uint[] ops)
    {
        uint target = ops[0];
        var decoration = (SpirvDecoration)ops[1];
        switch (decoration)
        {
            case SpirvDecoration.Block:
            case SpirvDecoration.BufferBlock:
                _blockStructs.Add(target);
                break;
            case SpirvDecoration.Binding when ops.Length >= 3:
                _binding[target] = (int)ops[2];
                break;
            case SpirvDecoration.DescriptorSet when ops.Length >= 3:
                _descriptorSet[target] = (int)ops[2];
                break;
            case SpirvDecoration.ArrayStride when ops.Length >= 3:
                _arrayStride[target] = (int)ops[2];
                break;
        }
    }

    private void CollectMemberDecorate(uint[] ops)
    {
        uint structId = ops[0];
        int memberIndex = (int)ops[1];
        var decoration = (SpirvDecoration)ops[2];
        switch (decoration)
        {
            case SpirvDecoration.Offset when ops.Length >= 4:
                AddMemberInt(_memberOffset, structId, memberIndex, (int)ops[3]);
                break;
            case SpirvDecoration.MatrixStride when ops.Length >= 4:
                AddMemberInt(_memberMatrixStride, structId, memberIndex, (int)ops[3]);
                break;
            case SpirvDecoration.ColMajor:
                AddMemberBool(_memberColMajor, structId, memberIndex, true);
                break;
            case SpirvDecoration.RowMajor:
                AddMemberBool(_memberColMajor, structId, memberIndex, false);
                break;
        }
    }

    private static void AddMemberInt(Dictionary<uint, Dictionary<int, int>> map, uint structId, int member, int value)
    {
        if (!map.TryGetValue(structId, out var dict))
            map[structId] = dict = new Dictionary<int, int>();
        dict[member] = value;
    }

    private static void AddMemberBool(Dictionary<uint, Dictionary<int, bool>> map, uint structId, int member, bool value)
    {
        if (!map.TryGetValue(structId, out var dict))
            map[structId] = dict = new Dictionary<int, bool>();
        dict[member] = value;
    }

    private static TextureDimension MapDim(SpirvDim dim) => dim switch
    {
        SpirvDim.Dim1D => TextureDimension.Texture1D,
        SpirvDim.Dim2D => TextureDimension.Texture2D,
        SpirvDim.Dim3D => TextureDimension.Texture3D,
        SpirvDim.Cube  => TextureDimension.TextureCube,
        _              => TextureDimension.Unknown,
    };
}
