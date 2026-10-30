#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// Every RDEF binding of an SM4/SM5 DXBC container that occupies <c>t</c> registers: textures and
/// typed buffers, <c>tbuffer</c>s, structured and byte-address buffers. <see cref="RdefReader"/>
/// keeps only textures and samplers (its parity contract with <c>D3DReflect</c>); the DirectX 11
/// texture slot re-assignment needs every resource that competes for a <c>t</c> register. Pure
/// managed; <c>null</c> when the container or its RDEF cannot be read.
/// </summary>
internal static class DxbcTRegisterBindings
{
    private const uint DxbcFourcc = 0x43425844; // 'DXBC'
    private const uint RdefFourcc = 0x46454452; // 'RDEF'

    /// <summary>One RDEF binding that occupies <c>t</c> registers.</summary>
    public readonly record struct Binding(string Name, int Slot, int Count);

    /// <summary>The <c>t</c>-register bindings of <paramref name="dxbc"/>, in RDEF order.</summary>
    public static IReadOnlyList<Binding>? Read(ReadOnlySpan<byte> dxbc)
    {
        ReadOnlySpan<byte> rdef = FindChunk(dxbc, RdefFourcc);
        if (rdef.Length < 16)
            return null;
        uint count = ReadU32(rdef, 8);
        uint offset = ReadU32(rdef, 12);
        if (count > 4096 || offset + count * 32L > rdef.Length)
            return null;
        var bindings = new List<Binding>();
        for (int i = 0; i < count; i++)
        {
            int rec = (int)offset + i * 32;
            uint nameOffset = ReadU32(rdef, rec);
            uint inputType = ReadU32(rdef, rec + 4);
            uint bindPoint = ReadU32(rdef, rec + 20);
            uint bindCount = ReadU32(rdef, rec + 24);
            // D3D_SIT_TBUFFER 1, TEXTURE 2, STRUCTURED 5, BYTEADDRESS 7: the t-register kinds.
            if (inputType is not (1 or 2 or 5 or 7))
                continue;
            if (nameOffset >= rdef.Length)
                return null;
            int end = rdef.Slice((int)nameOffset).IndexOf((byte)0);
            if (end < 0)
                return null;
            string name = System.Text.Encoding.UTF8.GetString(rdef.Slice((int)nameOffset, end));
            bindings.Add(new Binding(name, (int)bindPoint, (int)bindCount));
        }
        return bindings;
    }

    private static ReadOnlySpan<byte> FindChunk(ReadOnlySpan<byte> dxbc, uint wanted)
    {
        if (dxbc.Length < 32 || ReadU32(dxbc, 0) != DxbcFourcc)
            return default;
        uint chunkCount = ReadU32(dxbc, 28);
        if (chunkCount > 1024 || 32 + chunkCount * 4L > dxbc.Length)
            return default;
        for (int i = 0; i < chunkCount; i++)
        {
            uint offset = ReadU32(dxbc, 32 + i * 4);
            if (offset + 8L > dxbc.Length)
                return default;
            uint fourcc = ReadU32(dxbc, (int)offset);
            uint size = ReadU32(dxbc, (int)offset + 4);
            if (offset + 8L + size > dxbc.Length)
                return default;
            if (fourcc == wanted)
                return dxbc.Slice((int)offset + 8, (int)size);
        }
        return default;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
}
