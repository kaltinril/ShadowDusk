#nullable enable

using System.Buffers.Binary;
using System.Text;

namespace ShadowDusk.Core.Tests.Reflection;

/// <summary>
/// Builds small synthetic DXBC containers (RDEF / ISGN / OSGN chunks) byte-by-byte for
/// the pure <c>RdefReader</c> unit tests — the DXBC sibling of <c>Fx2SyntheticShaders</c>.
/// Layout follows the stable DXBC container / RDEF format (Wine d3dcompiler, vkd3d-shader
/// <c>tpf</c> writer); field placement was verified byte-level against d3dcompiler_47's
/// own emission (see <c>RdefReader</c>'s doc comment). Real-compiler coverage lives in
/// <c>DxbcReflectionParityTests</c> (Integration, Windows): these synthetic blobs keep
/// the unit suite pure (no disk, no native).
/// </summary>
internal static class DxbcSyntheticBlobs
{
    public const uint Ps50Target = 0xFFFF0500;
    public const uint Vs50Target = 0xFFFE0500;
    public const uint Ps41Target = 0xFFFF0401;

    // D3D_SHADER_INPUT_TYPE
    public const uint BindCbuffer = 0;
    public const uint BindTexture = 2;
    public const uint BindSampler = 3;

    public sealed record SynthType(
        ushort Class,
        ushort Type,
        ushort Rows,
        ushort Cols,
        ushort Elements,
        IReadOnlyList<(string Name, uint Offset, SynthType Type)>? Members = null);

    public sealed record SynthVar(string Name, uint StartOffset, uint Size, SynthType Type);

    public sealed record SynthCbuffer(string Name, uint Size, IReadOnlyList<SynthVar> Vars);

    public sealed record SynthBinding(string Name, uint InputType, uint Dimension, uint BindPoint);

    public sealed record SynthSigElement(
        string Name, uint Index, uint SystemValue, uint ComponentType, uint Register,
        byte Mask, byte ReadWriteMask);

    // -------------------------------------------------------------------------
    // Container
    // -------------------------------------------------------------------------

    /// <summary>Wraps chunk payloads in a DXBC container (zeroed hash, version 1).</summary>
    public static byte[] Container(params (string FourCC, byte[] Payload)[] chunks)
    {
        var w = new Writer();
        w.Ascii("DXBC");
        for (int i = 0; i < 4; i++) w.U32(0); // 16-byte hash (unchecked by the reader)
        w.U32(1);                             // container version
        int totalSizePos = w.ReserveU32();
        w.U32((uint)chunks.Length);

        int[] offsetSlots = new int[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
            offsetSlots[i] = w.ReserveU32();

        for (int i = 0; i < chunks.Length; i++)
        {
            w.Patch(offsetSlots[i], (uint)w.Position);
            w.Ascii(chunks[i].FourCC);
            w.U32((uint)chunks[i].Payload.Length);
            w.Bytes(chunks[i].Payload);
        }

        w.Patch(totalSizePos, (uint)w.Position);
        return w.ToArray();
    }

    // -------------------------------------------------------------------------
    // RDEF chunk payload
    // -------------------------------------------------------------------------

    public static byte[] Rdef(
        uint target,
        IReadOnlyList<SynthBinding> bindings,
        IReadOnlyList<SynthCbuffer> cbuffers)
    {
        bool sm5 = ((target >> 8) & 0xFF) >= 5;
        var w = new Writer();

        w.U32((uint)cbuffers.Count);
        int cbufferOffsetPos = w.ReserveU32();
        w.U32((uint)bindings.Count);
        int bindingOffsetPos = w.ReserveU32();
        w.U32(target);
        w.U32(0); // flags
        int creatorPos = w.ReserveU32();

        if (sm5)
        {
            // The 'RD11' block exactly as fxc/vkd3d emit it: magic + record sizes.
            w.U32(0x31314452); // 'RD11'
            w.U32(60); w.U32(24); w.U32(32); w.U32(40); w.U32(36); w.U32(12); w.U32(0);
        }

        // Resource bindings (32-byte records).
        w.Patch(bindingOffsetPos, (uint)w.Position);
        foreach (SynthBinding b in bindings)
        {
            w.StringRef(b.Name);
            w.U32(b.InputType);
            w.U32(b.InputType == BindTexture ? 5u : 0u); // return type (float)
            w.U32(b.Dimension);
            w.U32(b.InputType == BindTexture ? 0xFFFFFFFF : 0u); // num samples
            w.U32(b.BindPoint);
            w.U32(1); // bind count
            w.U32(0); // flags
        }

        // Constant-buffer records (24 bytes), then each buffer's variable array.
        w.Patch(cbufferOffsetPos, (uint)w.Position);
        var varOffsetSlots = new int[cbuffers.Count];
        foreach ((SynthCbuffer cb, int i) in cbuffers.Select((cb, i) => (cb, i)))
        {
            w.StringRef(cb.Name);
            w.U32((uint)cb.Vars.Count);
            varOffsetSlots[i] = w.ReserveU32();
            w.U32(cb.Size);
            w.U32(0); // flags
            w.U32(0); // type (D3D_CT_CBUFFER)
        }

        int varRecordSize = sm5 ? 40 : 24;
        var typeSlots = new List<(int Slot, SynthType Type)>();
        foreach ((SynthCbuffer cb, int i) in cbuffers.Select((cb, i) => (cb, i)))
        {
            w.Patch(varOffsetSlots[i], (uint)w.Position);
            foreach (SynthVar v in cb.Vars)
            {
                w.StringRef(v.Name);
                w.U32(v.StartOffset);
                w.U32(v.Size);
                w.U32(2); // flags (D3D_SVF_USED)
                typeSlots.Add((w.ReserveU32(), v.Type));
                w.U32(0); // default-value offset
                if (varRecordSize == 40)
                {
                    w.U32(0xFFFFFFFF); w.U32(0); w.U32(0xFFFFFFFF); w.U32(0); // SM5 extras
                }
            }
        }

        // Type records (16 bytes read by the reader; SM5 appends 20 unread bytes),
        // each struct followed by its 12-byte member records.
        foreach ((int slot, SynthType type) in typeSlots)
            w.Patch(slot, (uint)WriteType(w, type, sm5));

        w.PatchStringRef(creatorPos, "ShadowDusk synthetic");
        w.FlushStrings();
        return w.ToArray();
    }

    /// <summary>Writes a type record (nested member types first); returns its position.</summary>
    private static int WriteType(Writer w, SynthType type, bool sm5)
    {
        var memberTypePositions = new List<int>();
        if (type.Members is not null)
        {
            foreach ((_, _, SynthType memberType) in type.Members)
                memberTypePositions.Add(WriteType(w, memberType, sm5));
        }

        int recordPosition = w.Position;
        w.U16(type.Class);
        w.U16(type.Type);
        w.U16(type.Rows);
        w.U16(type.Cols);
        w.U16(type.Elements);
        w.U16((ushort)(type.Members?.Count ?? 0));
        int memberArraySlot = w.ReserveU32();
        if (sm5)
        {
            w.U32(0); w.U32(0); w.U32(0); w.U32(0); // SM5 extras (parent/interface info)
            w.StringRef("synthetic_type");          // type-name offset
        }

        if (type.Members is null || type.Members.Count == 0)
        {
            w.Patch(memberArraySlot, 0);
            return recordPosition;
        }

        w.Patch(memberArraySlot, (uint)w.Position);
        foreach (((string name, uint offset, _), int idx) in type.Members.Select((m, i) => (m, i)))
        {
            w.StringRef(name);
            w.U32((uint)memberTypePositions[idx]);
            w.U32(offset);
        }

        return recordPosition;
    }

    // -------------------------------------------------------------------------
    // Signature chunk payload (classic 24-byte ISGN/OSGN elements)
    // -------------------------------------------------------------------------

    public static byte[] Signature(params SynthSigElement[] elements)
    {
        var w = new Writer();
        w.U32((uint)elements.Length);
        w.U32(8); // offset to the first element
        foreach (SynthSigElement e in elements)
        {
            w.StringRef(e.Name);
            w.U32(e.Index);
            w.U32(e.SystemValue);
            w.U32(e.ComponentType);
            w.U32(e.Register);
            w.Byte(e.Mask);
            w.Byte(e.ReadWriteMask);
            w.U16(0); // padding
        }
        w.FlushStrings();
        return w.ToArray();
    }

    // -------------------------------------------------------------------------
    // Little byte writer with offset patching and a deferred string pool
    // -------------------------------------------------------------------------

    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];
        private readonly List<(int Slot, string Value)> _pendingStrings = [];
        private readonly Dictionary<string, uint> _stringOffsets = new(StringComparer.Ordinal);

        public int Position => _bytes.Count;

        public void Byte(byte v) => _bytes.Add(v);

        public void Bytes(ReadOnlySpan<byte> v) => _bytes.AddRange(v.ToArray());

        public void Ascii(string s) => Bytes(Encoding.ASCII.GetBytes(s));

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            Bytes(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            Bytes(b);
        }

        public int ReserveU32()
        {
            int pos = Position;
            U32(0xDEADBEEF);
            return pos;
        }

        public void Patch(int slot, uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            for (int i = 0; i < 4; i++)
                _bytes[slot + i] = b[i];
        }

        /// <summary>Reserves a u32 here that will point at <paramref name="s"/> in the pool.</summary>
        public void StringRef(string s) => _pendingStrings.Add((ReserveU32(), s));

        public void PatchStringRef(int slot, string s) => _pendingStrings.Add((slot, s));

        /// <summary>Appends the deduplicated string pool and patches every reference.</summary>
        public void FlushStrings()
        {
            foreach ((int slot, string value) in _pendingStrings)
            {
                if (!_stringOffsets.TryGetValue(value, out uint offset))
                {
                    offset = (uint)Position;
                    _stringOffsets[value] = offset;
                    Ascii(value);
                    Byte(0);
                }
                Patch(slot, offset);
            }
            _pendingStrings.Clear();
        }

        public byte[] ToArray()
        {
            FlushStrings();
            return [.. _bytes];
        }
    }
}
