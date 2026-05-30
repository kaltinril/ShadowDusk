#nullable enable

using System.Text;

namespace ShadowDusk.Core.Reflection.Spirv;

/// <summary>
/// A minimal, pure-managed SPIR-V binary reader. It validates the header, walks the
/// word stream once, and collects the instructions the reflection parser needs into
/// id-keyed lookup tables. It does NOT validate the module beyond the header magic.
/// </summary>
internal sealed class SpirvModule
{
    public const uint MagicNumber = 0x07230203;

    /// <summary>Raw instruction: opcode plus its operand words (operands exclude the leading opcode word).</summary>
    internal readonly struct Instruction
    {
        public Instruction(SpirvOpcode opcode, uint[] operands)
        {
            Opcode   = opcode;
            Operands = operands;
        }

        public SpirvOpcode Opcode   { get; }
        public uint[]      Operands { get; }
    }

    private readonly List<Instruction> _instructions = new();

    /// <summary>All instructions in stream order.</summary>
    public IReadOnlyList<Instruction> Instructions => _instructions;

    private SpirvModule() { }

    /// <summary>
    /// Parses a SPIR-V blob. Returns null when the blob is too small or the magic
    /// number is wrong (after attempting a byte-order swap).
    /// </summary>
    public static SpirvModule? TryParse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 20 || (blob.Length % 4) != 0)
            return null;

        // Read header word 0 to determine endianness. SPIR-V is defined as a stream
        // of 32-bit words; DXC emits little-endian. We support either by swapping.
        uint firstWord = ReadWordLittleEndian(blob, 0);
        bool swap;
        if (firstWord == MagicNumber)
            swap = false;
        else if (ReverseBytes(firstWord) == MagicNumber)
            swap = true;
        else
            return null;

        int wordCount = blob.Length / 4;
        var words = new uint[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            uint w = ReadWordLittleEndian(blob, i * 4);
            words[i] = swap ? ReverseBytes(w) : w;
        }

        var module = new SpirvModule();

        // Words 0..4 are the header (magic, version, generator, bound, schema).
        int pos = 5;
        while (pos < wordCount)
        {
            uint instrHeader = words[pos];
            ushort opcode = (ushort)(instrHeader & 0xFFFF);
            int instrWordCount = (int)(instrHeader >> 16);

            // A zero word count would mean no progress; guard against malformed input.
            if (instrWordCount == 0 || pos + instrWordCount > wordCount)
                break;

            var operands = new uint[instrWordCount - 1];
            Array.Copy(words, pos + 1, operands, 0, operands.Length);

            module._instructions.Add(new Instruction((SpirvOpcode)opcode, operands));
            pos += instrWordCount;
        }

        return module;
    }

    /// <summary>
    /// Decodes a SPIR-V literal string starting at <paramref name="operandStart"/> in the
    /// given operand array. SPIR-V strings are UTF-8, packed little-endian into 32-bit
    /// words, NUL-terminated.
    /// </summary>
    public static string DecodeString(uint[] operands, int operandStart)
    {
        var bytes = new List<byte>(16);
        for (int i = operandStart; i < operands.Length; i++)
        {
            uint word = operands[i];
            for (int b = 0; b < 4; b++)
            {
                byte ch = (byte)((word >> (b * 8)) & 0xFF);
                if (ch == 0)
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(ch);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static uint ReadWordLittleEndian(ReadOnlySpan<byte> blob, int byteOffset) =>
        (uint)(blob[byteOffset]
               | (blob[byteOffset + 1] << 8)
               | (blob[byteOffset + 2] << 16)
               | (blob[byteOffset + 3] << 24));

    private static uint ReverseBytes(uint value) =>
        (value >> 24)
        | ((value >> 8) & 0x0000FF00u)
        | ((value << 8) & 0x00FF0000u)
        | (value << 24);
}
