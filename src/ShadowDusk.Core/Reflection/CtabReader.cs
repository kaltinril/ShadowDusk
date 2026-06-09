#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Reads the CTAB constant-table comment out of a legacy D3D9 SM1–3 token stream — the
/// reflection source for the FNA fx_2_0 path. Layout per <c>docs/fx2-binary-format.md</c>
/// §11 (derived from MojoShader's <c>parse_constant_table</c>, which is what FNA itself runs
/// on these bytes at load time, and the documented <c>D3DXSHADER_CONSTANTTABLE</c> structs).
///
/// Only the leading comment block(s) directly after the version token are scanned: that is
/// where fxc and vkd3d both place the CTAB, and scanning the instruction stream would risk
/// misreading raw IEEE-float immediates (e.g. from <c>def</c>) as comment tokens.
/// </summary>
public static class CtabReader
{
    private const uint CtabFourcc = 0x42415443; // 'CTAB'
    private const uint CommentOpcode = 0x0000FFFE;
    private const uint EndToken = 0x0000FFFF;
    private const int CtabHeaderSize = 28;
    private const int ConstantInfoSize = 20;

    /// <summary>
    /// Parses the CTAB from <paramref name="d3d9Bytecode"/>. Fails (never throws) when the
    /// blob is not a D3D9 token stream or carries no readable CTAB — a shader without a CTAB
    /// would bind zero parameters under MojoShader, so the FNA pipeline treats that as an
    /// error rather than producing a silently-dead effect.
    /// </summary>
    public static Result<CtabTable, ShaderError> Read(ReadOnlySpan<byte> d3d9Bytecode, string sourceFile)
    {
        if (d3d9Bytecode.Length < 8 || d3d9Bytecode.Length % 4 != 0)
            return Fail(sourceFile, "blob is too small or not dword-aligned");

        uint version = ReadU32(d3d9Bytecode, 0);
        uint kind = version >> 16;
        if (kind != 0xFFFF && kind != 0xFFFE)
            return Fail(sourceFile, $"not a D3D9 token stream (version token 0x{version:X8})");

        // Walk the leading comment blocks.
        int pos = 4;
        while (pos + 4 <= d3d9Bytecode.Length)
        {
            uint token = ReadU32(d3d9Bytecode, pos);
            if (token == EndToken)
                break;
            if ((token & 0xFFFF) != CommentOpcode || (token & 0x8000_0000) != 0)
                break; // first real instruction — no CTAB ahead of it

            int commentDwords = (int)((token >> 16) & 0x7FFF);
            int payloadStart = pos + 4;
            long payloadEnd = payloadStart + (long)commentDwords * 4;
            if (payloadEnd > d3d9Bytecode.Length)
                return Fail(sourceFile, "comment block runs past the end of the blob");

            if (commentDwords >= 1 && ReadU32(d3d9Bytecode, payloadStart) == CtabFourcc)
            {
                // The CTAB region: everything after the fourcc; all CTAB offsets are
                // relative to its start.
                ReadOnlySpan<byte> region = d3d9Bytecode.Slice(payloadStart + 4, (commentDwords - 1) * 4);
                return ParseRegion(region, version, sourceFile);
            }

            pos = (int)payloadEnd;
        }

        return Fail(sourceFile, "no CTAB constant table found in the leading comment blocks");
    }

    private static Result<CtabTable, ShaderError> ParseRegion(
        ReadOnlySpan<byte> region, uint blobVersion, string sourceFile)
    {
        if (region.Length < CtabHeaderSize)
            return Fail(sourceFile, "CTAB region smaller than the 28-byte header");

        uint size = ReadU32(region, 0);
        uint creatorOfs = ReadU32(region, 4);
        uint ctabVersion = ReadU32(region, 8);
        uint constantCount = ReadU32(region, 12);
        uint constantInfoOfs = ReadU32(region, 16);
        // +20 Flags — not read (MojoShader ignores it too).
        uint targetOfs = ReadU32(region, 24);

        if (size != CtabHeaderSize)
            return Fail(sourceFile, $"CTAB header size {size} != 28");
        if (ctabVersion != blobVersion)
            return Fail(sourceFile,
                $"CTAB version 0x{ctabVersion:X8} does not match the shader version 0x{blobVersion:X8}");
        if (constantCount > 1_000_000)
            return Fail(sourceFile, $"implausible CTAB constant count {constantCount}");
        if (constantInfoOfs + constantCount * (long)ConstantInfoSize > region.Length)
            return Fail(sourceFile, "CTAB constant records run past the region end");

        if (!TryReadString(region, creatorOfs, out string creator))
            return Fail(sourceFile, "CTAB creator string out of bounds");
        if (!TryReadString(region, targetOfs, out string target))
            return Fail(sourceFile, "CTAB target string out of bounds");

        var constants = new List<CtabConstant>((int)constantCount);
        for (int i = 0; i < constantCount; i++)
        {
            int rec = (int)constantInfoOfs + i * ConstantInfoSize;
            uint nameOfs = ReadU32(region, rec);
            ushort registerSet = ReadU16(region, rec + 4);
            ushort registerIndex = ReadU16(region, rec + 6);
            ushort registerCount = ReadU16(region, rec + 8);
            // +10 Reserved
            uint typeInfoOfs = ReadU32(region, rec + 12);
            uint defaultOfs = ReadU32(region, rec + 16);

            if (!TryReadString(region, nameOfs, out string name) || name.Length == 0)
                return Fail(sourceFile, $"CTAB constant #{i} has an unreadable name");
            if (registerSet > (ushort)CtabRegisterSet.Sampler)
                return Fail(sourceFile, $"CTAB constant '{name}' has unknown register set {registerSet}");
            if (typeInfoOfs + 16 > region.Length)
                return Fail(sourceFile, $"CTAB constant '{name}' type info out of bounds");

            ushort cls = ReadU16(region, (int)typeInfoOfs);
            ushort type = ReadU16(region, (int)typeInfoOfs + 2);
            ushort rows = ReadU16(region, (int)typeInfoOfs + 4);
            ushort columns = ReadU16(region, (int)typeInfoOfs + 6);
            ushort elements = ReadU16(region, (int)typeInfoOfs + 8);
            // +10 StructMembers, +12 StructMemberInfo — struct constants are not modeled;
            // the FNA pipeline rejects struct globals up front (fail loudly).

            int normalizedElements = Math.Max(1, (int)elements);

            // Default values: propagate only the unambiguous single-register-row shapes
            // (scalar/vector, non-array). The CTAB layout of matrix/array defaults is the
            // unverified F2 ambiguity — leaving them null keeps defaults at zero, the same
            // behavior as the MGFX writer, instead of risking a silent wrong-major bake.
            IReadOnlyList<float>? defaultValue = null;
            if (defaultOfs != 0 && rows == 1 && elements <= 1 && cls <= 1 /* scalar/vector */)
            {
                long defEnd = defaultOfs + (long)columns * 4;
                if (defEnd <= region.Length)
                {
                    var values = new float[columns];
                    for (int c = 0; c < columns; c++)
                        values[c] = BinaryPrimitives.ReadSingleLittleEndian(
                            region.Slice((int)defaultOfs + c * 4, 4));
                    defaultValue = values;
                }
            }

            constants.Add(new CtabConstant(
                Name: name,
                RegisterSet: (CtabRegisterSet)registerSet,
                RegisterIndex: registerIndex,
                RegisterCount: registerCount,
                Class: cls,
                Type: type,
                Rows: rows,
                Columns: columns,
                Elements: normalizedElements,
                DefaultValue: defaultValue));
        }

        return Result<CtabTable, ShaderError>.Ok(
            new CtabTable(blobVersion, creator, target, constants));
    }

    private static bool TryReadString(ReadOnlySpan<byte> region, uint offset, out string value)
    {
        value = string.Empty;
        if (offset == 0)
            return true; // absent string — fine for creator/target
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

    private static Result<CtabTable, ShaderError> Fail(string sourceFile, string detail) =>
        Result<CtabTable, ShaderError>.Fail(new ShaderError(
            File: sourceFile,
            Line: 0,
            Column: 0,
            Code: "SD0301",
            Message: "D3D9 shader reflection failed: " + detail));
}
