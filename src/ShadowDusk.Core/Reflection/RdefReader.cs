#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Pure-managed DXBC reflection: parses the <c>DXBC</c> container's <c>RDEF</c>
/// (resource definition) and <c>ISGN</c>/<c>OSGN</c> (signature) chunks into the same
/// <see cref="ReflectedEffect"/> shape <c>D3DReflect</c> produced — the SM4/SM5 sibling
/// of <see cref="CtabReader"/> (Phase 18 Track A). This removes the Windows-only
/// d3dcompiler_47 P/Invoke from the DirectX pipeline, so DX11 <c>.mgfx</c> compiles run
/// on Linux/macOS (and, being dependency-free managed code, in WASM).
///
/// Layout sources: the DXBC container / RDEF format is stable and documented (Wine's
/// d3dcompiler reflection implementation, vkd3d-shader's <c>tpf</c>/<c>dxbc</c> writers,
/// and public DXBC container format references). Field semantics were verified byte-level
/// against d3dcompiler_47's own emission and <c>ID3D11ShaderReflection</c>'s readback;
/// oracle parity is enforced by <c>DxbcReflectionParityTests</c> (managed output deeply
/// equal to <c>D3DReflect</c>'s for both d3dcompiler_47 and vkd3d DXBC, on Windows).
///
/// Behavioral quirks preserved deliberately (the bar is "identical to D3DReflect +
/// the previous extractor", not "looks right"):
/// <list type="bullet">
/// <item>Array variables report a 16-byte-aligned size (the previous extractor rounded
/// <c>D3DReflect</c>'s unpadded trailing element up; see <c>VariableReflection</c>).</item>
/// <item>Empty constant buffers are dropped — mgfxc emits no cbuffer record for an empty
/// <c>$Globals</c> (e.g. a texture-only PS), so the DX <c>.mgfx</c> must match.</item>
/// <item><c>D3DReflect</c> fixes up signature system values fxc stores as 0 by semantic
/// name (<c>SV_Target</c> → <c>Target</c>, <c>SV_Depth</c> → <c>Depth</c>, …) —
/// reproduced in <see cref="FixUpSystemValue"/>.</item>
/// </list>
/// </summary>
public static class RdefReader
{
    private const uint DxbcFourcc = 0x43425844; // 'DXBC'
    private const uint RdefFourcc = 0x46454452; // 'RDEF'
    private const uint IsgnFourcc = 0x4E475349; // 'ISGN'
    private const uint OsgnFourcc = 0x4E47534F; // 'OSGN'
    private const uint Isg1Fourcc = 0x31475349; // 'ISG1' (SM5.1 / min-precision variant)
    private const uint Osg1Fourcc = 0x3147534F; // 'OSG1'
    private const uint Osg5Fourcc = 0x3547534F; // 'OSG5' (stream-out variant)

    private const int ContainerHeaderSize = 32;  // fourcc + 16-byte hash + version + size + chunk count
    private const int RdefHeaderSize      = 28;  // counts/offsets + target + flags + creator
    private const int Rd11ExtraSize       = 32;  // 8 extra dwords on SM5 ('RD11') RDEF headers
    private const int BindingRecordSize   = 32;
    private const int CbufferRecordSize   = 24;
    private const int VariableRecordSizeSm4 = 24;
    private const int VariableRecordSizeSm5 = 40; // SM4 + StartTexture/TextureSize/StartSampler/SamplerSize
    private const int TypeRecordSize      = 16;  // the portion read; SM5 appends 20 unread bytes
    private const int MemberRecordSize    = 12;

    // D3D_SHADER_INPUT_TYPE values present in SM5 RDEF binding records.
    private const uint InputTypeCbuffer = 0;
    private const uint InputTypeTexture = 2;
    private const uint InputTypeSampler = 3;

    /// <summary>
    /// Parses the reflection chunks of <paramref name="dxbc"/> into a
    /// <see cref="ReflectedEffect"/> (with <see cref="ReflectedEffect.Parameters"/> left
    /// empty — the parameter list is assembled downstream, exactly as before). Fails
    /// (never throws) when the blob is not a DXBC container or its reflection data is
    /// malformed.
    /// </summary>
    /// <param name="dxbc">A complete SM4/SM5 DXBC module.</param>
    /// <param name="sourceFile">The source file to attribute errors to (may be empty).</param>
    public static Result<ReflectedEffect, ShaderError> Read(ReadOnlySpan<byte> dxbc, string sourceFile = "")
    {
        if (dxbc.Length < ContainerHeaderSize)
            return Fail(sourceFile, "blob is smaller than the DXBC container header");
        if (ReadU32(dxbc, 0) != DxbcFourcc)
            return Fail(sourceFile, $"not a DXBC container (fourcc 0x{ReadU32(dxbc, 0):X8})");

        uint totalSize = ReadU32(dxbc, 24);
        if (totalSize > dxbc.Length)
            return Fail(sourceFile, $"container declares {totalSize} bytes but only {dxbc.Length} are present");

        uint chunkCount = ReadU32(dxbc, 28);
        if (chunkCount > 1024)
            return Fail(sourceFile, $"implausible chunk count {chunkCount}");
        if (ContainerHeaderSize + chunkCount * 4L > dxbc.Length)
            return Fail(sourceFile, "chunk offset table runs past the end of the blob");

        ReadOnlySpan<byte> rdef = default, isgn = default, osgn = default;
        bool hasRdef = false;
        uint inputElementSize = 24, outputElementSize = 24;

        for (int i = 0; i < chunkCount; i++)
        {
            uint chunkOffset = ReadU32(dxbc, ContainerHeaderSize + i * 4);
            if (chunkOffset + 8L > dxbc.Length)
                return Fail(sourceFile, $"chunk #{i} header at offset {chunkOffset} is out of bounds");

            uint fourcc = ReadU32(dxbc, (int)chunkOffset);
            uint size   = ReadU32(dxbc, (int)chunkOffset + 4);
            if (chunkOffset + 8L + size > dxbc.Length)
                return Fail(sourceFile, $"chunk #{i} (0x{fourcc:X8}) runs past the end of the blob");

            ReadOnlySpan<byte> data = dxbc.Slice((int)chunkOffset + 8, (int)size);
            switch (fourcc)
            {
                case RdefFourcc:
                    rdef = data; hasRdef = true;
                    break;
                case IsgnFourcc: isgn = data; inputElementSize  = 24; break;
                case Isg1Fourcc: isgn = data; inputElementSize  = 32; break; // + Stream, MinPrecision
                case OsgnFourcc: osgn = data; outputElementSize = 24; break;
                case Osg5Fourcc: osgn = data; outputElementSize = 28; break; // + Stream
                case Osg1Fourcc: osgn = data; outputElementSize = 32; break;
            }
        }

        if (!hasRdef)
            return Fail(sourceFile, "no RDEF chunk found (was the blob compiled with reflection stripped?)");

        var cbuffers = new List<ConstantBufferReflection>();
        var textures = new List<TextureReflection>();
        var samplers = new List<SamplerReflection>();
        string? rdefError = ParseRdef(rdef, cbuffers, textures, samplers);
        if (rdefError is not null)
            return Fail(sourceFile, rdefError);

        var inputSig  = new List<SignatureParameterReflection>();
        var outputSig = new List<SignatureParameterReflection>();
        string? sigError = ParseSignature(isgn, inputElementSize, inputSig)
                        ?? ParseSignature(osgn, outputElementSize, outputSig);
        if (sigError is not null)
            return Fail(sourceFile, sigError);

        return Result<ReflectedEffect, ShaderError>.Ok(new ReflectedEffect
        {
            ConstantBuffers = cbuffers,
            Textures        = textures,
            Samplers        = samplers,
            InputSignature  = inputSig,
            OutputSignature = outputSig,
            Parameters      = Array.Empty<ParameterReflection>(),
        });
    }

    // -------------------------------------------------------------------------
    // RDEF — constant buffers + resource bindings
    // -------------------------------------------------------------------------

    private static string? ParseRdef(
        ReadOnlySpan<byte> rdef,
        List<ConstantBufferReflection> cbuffers,
        List<TextureReflection> textures,
        List<SamplerReflection> samplers)
    {
        if (rdef.Length < RdefHeaderSize)
            return "RDEF chunk smaller than the 28-byte header";

        uint cbufferCount  = ReadU32(rdef, 0);
        uint cbufferOffset = ReadU32(rdef, 4);
        uint bindingCount  = ReadU32(rdef, 8);
        uint bindingOffset = ReadU32(rdef, 12);
        uint target        = ReadU32(rdef, 16);
        // +20 flags, +24 creator-string offset — not part of ReflectedEffect.
        // SM5 RDEF carries an extra 32-byte 'RD11' block (magic + record sizes) after the
        // header; it only changes the variable-record stride, handled below.

        int versionMajor = (int)((target >> 8) & 0xFF);
        int variableRecordSize = versionMajor >= 5 ? VariableRecordSizeSm5 : VariableRecordSizeSm4;

        if (cbufferCount > 65536 || bindingCount > 65536)
            return $"implausible RDEF counts (cbuffers={cbufferCount}, bindings={bindingCount})";

        // Resource bindings first: textures/samplers, and the cbuffer name → slot map
        // (replicating the previous extractor's BuildCbufferSlots; last-wins on duplicates).
        var cbufferSlots = new Dictionary<string, int>(StringComparer.Ordinal);

        if (bindingCount > 0 && bindingOffset + bindingCount * (long)BindingRecordSize > rdef.Length)
            return "RDEF resource-binding records run past the chunk end";

        for (int i = 0; i < bindingCount; i++)
        {
            int rec = (int)bindingOffset + i * BindingRecordSize;
            uint nameOffset = ReadU32(rdef, rec);
            uint inputType  = ReadU32(rdef, rec + 4);
            // +8 return type, +16 num samples, +24 bind count, +28 flags — unused.
            uint dimension  = ReadU32(rdef, rec + 12);
            uint bindPoint  = ReadU32(rdef, rec + 20);

            if (!TryReadString(rdef, nameOffset, out string name))
                return $"RDEF resource binding #{i} has an unreadable name";

            switch (inputType)
            {
                case InputTypeCbuffer:
                    cbufferSlots[name] = (int)bindPoint;
                    break;
                case InputTypeTexture:
                    textures.Add(new TextureReflection
                    {
                        Name      = name,
                        BindSlot  = (int)bindPoint,
                        Dimension = MapSrvDimension(dimension),
                    });
                    break;
                case InputTypeSampler:
                    samplers.Add(new SamplerReflection
                    {
                        Name     = name,
                        BindSlot = (int)bindPoint,
                    });
                    break;
                // Other binding types (tbuffers, UAVs, …) are ignored, as before.
            }
        }

        if (cbufferCount > 0 && cbufferOffset + cbufferCount * (long)CbufferRecordSize > rdef.Length)
            return "RDEF constant-buffer records run past the chunk end";

        for (int i = 0; i < cbufferCount; i++)
        {
            int rec = (int)cbufferOffset + i * CbufferRecordSize;
            uint nameOffset     = ReadU32(rdef, rec);
            uint variableCount  = ReadU32(rdef, rec + 4);
            uint variableOffset = ReadU32(rdef, rec + 8);
            uint sizeBytes      = ReadU32(rdef, rec + 12);
            // +16 flags, +20 type (cbuffer/tbuffer) — D3DReflect exposed both but the
            // effect pipeline never read them.

            if (!TryReadString(rdef, nameOffset, out string name))
                return $"RDEF constant buffer #{i} has an unreadable name";

            // mgfxc emits NO cbuffer record for a shader whose $Globals is empty
            // (e.g. a texture-only PS). Drop empty cbuffers so the DX .mgfx matches.
            if (variableCount == 0)
                continue;

            if (variableCount > 65536)
                return $"implausible variable count {variableCount} in cbuffer '{name}'";
            if (variableOffset + variableCount * (long)variableRecordSize > rdef.Length)
                return $"variable records of cbuffer '{name}' run past the chunk end";

            var variables = new List<VariableReflection>((int)variableCount);
            for (int j = 0; j < variableCount; j++)
            {
                int varRec = (int)variableOffset + j * variableRecordSize;
                uint varNameOffset = ReadU32(rdef, varRec);
                uint startOffset   = ReadU32(rdef, varRec + 4);
                uint varSize       = ReadU32(rdef, varRec + 8);
                // +12 flags, +20 default-value offset — unused (defaults are zeroed by
                // the MGFX writer, matching the previous extractor).
                uint typeOffset    = ReadU32(rdef, varRec + 16);

                if (!TryReadString(rdef, varNameOffset, out string varName))
                    return $"variable #{j} of cbuffer '{name}' has an unreadable name";

                Result<VariableReflection, string> variable = ParseVariable(
                    rdef, varName, (int)startOffset, (int)varSize, typeOffset, depth: 0);
                if (variable.IsFailure)
                    return variable.Error;

                variables.Add(variable.Value);
            }

            cbuffers.Add(new ConstantBufferReflection
            {
                Name      = name,
                SizeBytes = (int)sizeBytes,
                BindSlot  = cbufferSlots.TryGetValue(name, out int slot) ? slot : 0,
                Variables = variables,
            });
        }

        return null;
    }

    private static Result<VariableReflection, string> ParseVariable(
        ReadOnlySpan<byte> rdef,
        string name,
        int startOffset,
        int sizeBytes,
        uint typeOffset,
        int depth)
    {
        if (typeOffset + TypeRecordSize > rdef.Length)
            return Result<VariableReflection, string>.Fail(
                $"type record of variable '{name}' is out of bounds");

        ushort cls         = ReadU16(rdef, (int)typeOffset);
        ushort type        = ReadU16(rdef, (int)typeOffset + 2);
        ushort rows        = ReadU16(rdef, (int)typeOffset + 4);
        ushort columns     = ReadU16(rdef, (int)typeOffset + 6);
        ushort elements    = ReadU16(rdef, (int)typeOffset + 8);
        ushort memberCount = ReadU16(rdef, (int)typeOffset + 10);
        uint memberOffset  = ReadU32(rdef, (int)typeOffset + 12);

        if (!TryMapClass(cls, out EffectParameterClass parameterClass))
            return Result<VariableReflection, string>.Fail(
                $"variable '{name}' has an unmapped shader variable class {cls}");
        if (!TryMapType(type, out EffectParameterType parameterType))
            return Result<VariableReflection, string>.Fail(
                $"variable '{name}' has an unmapped shader variable type {type}");

        Result<IReadOnlyList<VariableReflection>?, string> members = ParseStructMembers(
            rdef, name, parameterClass, memberCount, memberOffset, depth);
        if (members.IsFailure)
            return Result<VariableReflection, string>.Fail(members.Error);

        return Result<VariableReflection, string>.Ok(new VariableReflection
        {
            Name           = name,
            StartOffset    = startOffset,
            // Arrays report their last element unpadded; round up to the 16-byte register
            // boundary, exactly as the previous extractor did with D3DReflect's Size.
            SizeBytes      = elements > 0 ? (sizeBytes + 15) & ~15 : sizeBytes,
            ParameterClass = parameterClass,
            ParameterType  = parameterType,
            Rows           = rows,
            Columns        = columns,
            Elements       = elements,
            Members        = members.Value,
        });
    }

    private static Result<IReadOnlyList<VariableReflection>?, string> ParseStructMembers(
        ReadOnlySpan<byte> rdef,
        string ownerName,
        EffectParameterClass ownerClass,
        ushort memberCount,
        uint memberOffset,
        int depth)
    {
        if (ownerClass != EffectParameterClass.Struct || memberCount == 0)
            return Result<IReadOnlyList<VariableReflection>?, string>.Ok(null);
        if (depth > 16)
            return Result<IReadOnlyList<VariableReflection>?, string>.Fail(
                $"struct '{ownerName}' exceeds the maximum member nesting depth");
        if (memberOffset + memberCount * (long)MemberRecordSize > rdef.Length)
            return Result<IReadOnlyList<VariableReflection>?, string>.Fail(
                $"member records of struct '{ownerName}' run past the chunk end");

        var members = new List<VariableReflection>(memberCount);
        for (int k = 0; k < memberCount; k++)
        {
            int rec = (int)memberOffset + k * MemberRecordSize;
            uint nameOffset       = ReadU32(rdef, rec);
            uint memberTypeOffset = ReadU32(rdef, rec + 4);
            uint byteOffset       = ReadU32(rdef, rec + 8);

            if (!TryReadString(rdef, nameOffset, out string memberName))
                return Result<IReadOnlyList<VariableReflection>?, string>.Fail(
                    $"member #{k} of struct '{ownerName}' has an unreadable name");

            // Mirrors the previous extractor: a member's StartOffset is its offset within
            // the parent struct, and its SizeBytes is 0 (D3DReflect exposes no member size).
            Result<VariableReflection, string> member = ParseVariable(
                rdef, memberName, (int)byteOffset, sizeBytes: 0, memberTypeOffset, depth + 1);
            if (member.IsFailure)
                return Result<IReadOnlyList<VariableReflection>?, string>.Fail(member.Error);

            members.Add(member.Value with { SizeBytes = 0 });
        }

        return Result<IReadOnlyList<VariableReflection>?, string>.Ok(members);
    }

    // -------------------------------------------------------------------------
    // ISGN / OSGN — input/output signatures
    // -------------------------------------------------------------------------

    private static string? ParseSignature(
        ReadOnlySpan<byte> chunk,
        uint elementSize,
        List<SignatureParameterReflection> parameters)
    {
        if (chunk.IsEmpty)
            return null; // chunk absent — signature stays empty

        if (chunk.Length < 8)
            return "signature chunk smaller than its 8-byte header";

        uint count = ReadU32(chunk, 0);
        // +4: offset to the first element (always 8 in practice) — not needed.
        if (count > 4096)
            return $"implausible signature element count {count}";
        if (8 + count * (long)elementSize > chunk.Length)
            return "signature elements run past the chunk end";

        // ISG1/OSG1 prefix each element with a Stream dword (OSG5 likewise); the classic
        // ISGN/OSGN element starts at the semantic-name offset directly.
        int fieldBase = elementSize >= 28 ? 4 : 0;

        for (int i = 0; i < count; i++)
        {
            int rec = 8 + (int)(i * elementSize) + fieldBase;
            uint nameOffset    = ReadU32(chunk, rec);
            uint semanticIndex = ReadU32(chunk, rec + 4);
            uint systemValue   = ReadU32(chunk, rec + 8);
            uint componentType = ReadU32(chunk, rec + 12);
            uint register      = ReadU32(chunk, rec + 16);
            byte mask          = chunk[rec + 20];
            // +21 read/write mask, +22 padding — not part of SignatureParameterReflection.

            if (!TryReadString(chunk, nameOffset, out string semanticName))
                return $"signature element #{i} has an unreadable semantic name";

            parameters.Add(new SignatureParameterReflection
            {
                SemanticName  = semanticName,
                SemanticIndex = (int)semanticIndex,
                Register      = unchecked((int)register), // SV_Depth uses 0xFFFFFFFF → -1
                SystemValue   = SystemValueName(FixUpSystemValue(systemValue, semanticName)),
                ComponentType = ComponentTypeName(componentType),
                Mask          = mask,
            });
        }

        return null;
    }

    /// <summary>
    /// fxc stores a system value of 0 (<c>Undefined</c>) for the pixel-shader output
    /// semantics, but <c>D3DReflect</c> fixes them up from the semantic name
    /// (<c>SV_Target</c> reflects as <c>Target</c>, etc. — verified against
    /// d3dcompiler_47). Reproduced here so the managed signature matches the oracle's.
    /// </summary>
    private static uint FixUpSystemValue(uint stored, string semanticName)
    {
        if (stored != 0)
            return stored;

        return semanticName.ToUpperInvariant() switch
        {
            "SV_TARGET"            => 64, // D3D_NAME_TARGET
            "SV_DEPTH"             => 65, // D3D_NAME_DEPTH
            "SV_COVERAGE"          => 66, // D3D_NAME_COVERAGE
            "SV_DEPTHGREATEREQUAL" => 67, // D3D_NAME_DEPTH_GREATER_EQUAL
            "SV_DEPTHLESSEQUAL"    => 68, // D3D_NAME_DEPTH_LESS_EQUAL
            "SV_STENCILREF"        => 69, // D3D_NAME_STENCIL_REF
            _                      => stored,
        };
    }

    // -------------------------------------------------------------------------
    // Value mapping
    // -------------------------------------------------------------------------

    private static bool TryMapClass(ushort cls, out EffectParameterClass mapped)
    {
        // D3D_SHADER_VARIABLE_CLASS values, mapped exactly as the previous extractor's
        // MapClass (interface classes were unmapped there too — they threw, here we fail).
        switch (cls)
        {
            case 0: mapped = EffectParameterClass.Scalar; return true;
            case 1: mapped = EffectParameterClass.Vector; return true;
            case 2: mapped = EffectParameterClass.Matrix; return true; // row-major
            case 3: mapped = EffectParameterClass.Matrix; return true; // column-major
            case 4: mapped = EffectParameterClass.Object; return true;
            case 5: mapped = EffectParameterClass.Struct; return true;
            default: mapped = default; return false;
        }
    }

    private static bool TryMapType(ushort type, out EffectParameterType mapped)
    {
        // D3D_SHADER_VARIABLE_TYPE values, mapped exactly as the previous extractor's
        // MapType (uint folds into Int32, matching mgfxc/MonoGame's parameter model).
        switch (type)
        {
            case 0:  mapped = EffectParameterType.Void;        return true;
            case 1:  mapped = EffectParameterType.Bool;        return true;
            case 2:  mapped = EffectParameterType.Int32;       return true;
            case 19: mapped = EffectParameterType.Int32;       return true; // uint
            case 3:  mapped = EffectParameterType.Single;      return true;
            case 4:  mapped = EffectParameterType.String;      return true;
            case 5:  mapped = EffectParameterType.Texture;     return true;
            case 6:  mapped = EffectParameterType.Texture1D;   return true;
            case 7:  mapped = EffectParameterType.Texture2D;   return true;
            case 8:  mapped = EffectParameterType.Texture3D;   return true;
            case 9:  mapped = EffectParameterType.TextureCube; return true;
            default: mapped = default; return false;
        }
    }

    private static TextureDimension MapSrvDimension(uint dim) =>
        // D3D_SRV_DIMENSION values, folded as the previous extractor's MapSrvDimension
        // (arrays/multisample collapse onto their base dimensionality).
        dim switch
        {
            2  => TextureDimension.Texture1D,   // TEXTURE1D
            3  => TextureDimension.Texture1D,   // TEXTURE1DARRAY
            4  => TextureDimension.Texture2D,   // TEXTURE2D
            5  => TextureDimension.Texture2D,   // TEXTURE2DARRAY
            6  => TextureDimension.Texture2D,   // TEXTURE2DMS
            7  => TextureDimension.Texture2D,   // TEXTURE2DMSARRAY
            8  => TextureDimension.Texture3D,   // TEXTURE3D
            9  => TextureDimension.TextureCube, // TEXTURECUBE
            10 => TextureDimension.TextureCube, // TEXTURECUBEARRAY
            _  => TextureDimension.Unknown,
        };

    /// <summary>
    /// D3D_NAME values → the exact strings the previous extractor produced
    /// (<c>Vortice.Direct3D.SystemValueType.ToString()</c>); unknown values render
    /// numerically, matching .NET's undefined-enum-value ToString.
    /// </summary>
    private static string SystemValueName(uint value) =>
        value switch
        {
            0  => "Undefined",
            1  => "Position",
            2  => "ClipDistance",
            3  => "CullDistance",
            4  => "RenderTargetArrayIndex",
            5  => "ViewportArrayIndex",
            6  => "VertexId",
            7  => "PrimitiveId",
            8  => "InstanceId",
            9  => "IsFrontFace",
            10 => "SampleIndex",
            11 => "FinalQuadEdgeTessfactor",
            12 => "FinalQuadInsideTessfactor",
            13 => "FinalTriEdgeTessfactor",
            14 => "FinalTriInsideTessfactor",
            15 => "FinalLineDetailTessfactor",
            16 => "FinalLineDensityTessfactor",
            23 => "Barycentrics",
            24 => "Shadingrate",
            25 => "Cullprimitive",
            64 => "Target",
            65 => "Depth",
            66 => "Coverage",
            67 => "DepthGreaterEqual",
            68 => "DepthLessEqual",
            69 => "StencilRef",
            70 => "InnerCoverage",
            _  => value.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// D3D_REGISTER_COMPONENT_TYPE values → the exact strings the previous extractor
    /// produced (<c>Vortice.Direct3D.RegisterComponentType.ToString()</c>).
    /// </summary>
    private static string ComponentTypeName(uint value) =>
        value switch
        {
            0 => "Unknown",
            1 => "UInt32",
            2 => "SInt32",
            3 => "Float32",
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

    // -------------------------------------------------------------------------
    // Primitives
    // -------------------------------------------------------------------------

    private static bool TryReadString(ReadOnlySpan<byte> region, uint offset, out string value)
    {
        value = string.Empty;
        if (offset >= region.Length)
            return false;

        int end = region[(int)offset..].IndexOf((byte)0);
        if (end < 0)
            return false; // unterminated — would run past the region

        value = Encoding.ASCII.GetString(region.Slice((int)offset, end));
        return true;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

    private static ushort ReadU16(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));

    private static Result<ReflectedEffect, ShaderError> Fail(string sourceFile, string detail) =>
        Result<ReflectedEffect, ShaderError>.Fail(new ShaderError(
            File:    sourceFile,
            Line:    0,
            Column:  0,
            Code:    "SD0101",
            Message: "DXBC reflection failed: " + detail));
}
