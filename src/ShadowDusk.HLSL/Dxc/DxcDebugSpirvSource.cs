#nullable enable

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using ShadowDusk.Core;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>
/// Issue #343: keeps the <c>OpSource</c> text of a SPIR-V compile with debug information
/// (<c>-spirv -Zi</c>, the OpenGL and Vulkan targets with <c>Debug</c>) a function of the
/// compiled source alone, never of the files on the host's disk.
///
/// <para>DXC's SPIR-V emitter (pin <c>e043f4a1</c>, <c>EmitVisitor.cpp</c>) fills every
/// <c>OpSource</c> by READING THE FILE it names (<c>ReadSourceCode</c>:
/// <c>CreateBlobFromFile</c> through a leaf-name load of <c>libdxcompiler</c>), and falls back
/// to the in-memory text only when that load or that read fails. It does so for two kinds of
/// name: the main input (DXC's default <c>hlsl.hlsl</c> when no input name is given, resolved
/// against the working directory), and every file a <c>#line</c> directive names (one
/// <c>OpSource</c> per presumed file, emitted with no text of its own, so a successful read is
/// the only text it ever gets). ShadowDusk's compiled source carries <c>#line</c> directives
/// naming the consumer's <c>SourceFileName</c> and its includes, and the text DXC's own
/// <c>-P</c> leaves behind names <c>hlsl.hlsl</c>. Where the leaf-name load succeeds
/// (Windows; macOS 14+, measured in issue #331) whatever file sits at those names on disk
/// became the output's <c>OpSource</c> text: a decoy <c>hlsl.hlsl</c> in the working
/// directory replaced the compiled source, and a file at a relative <c>SourceFileName</c>
/// was embedded verbatim; on a plain Linux host the load fails and neither happened.</para>
///
/// <para>Two measures make every host produce the Linux bytes' shape:
/// <list type="number">
/// <item>The main input is named <see cref="InputName"/>, which no host can open: on Linux
/// and macOS it lies under <c>/dev/null</c>, which is not a directory (<c>ENOTDIR</c>); on
/// Windows its <c>&lt;</c> and <c>&gt;</c> are characters no Win32 file name may contain
/// (<c>ERROR_INVALID_NAME</c>), and its first components resolve under the current drive's
/// root. DXC's own virtual file system serves the compile from memory under any name, so the
/// read for the main <c>OpSource</c> fails everywhere and the in-memory text is embedded.</item>
/// <item>Every other <c>OpSource</c> loses whatever text the read found
/// (<see cref="DropFileReadText"/>), which is exactly what those instructions carry when the
/// file does not exist. The main <c>OpSource</c> already holds the full preprocessed text,
/// includes and all.</item>
/// </list>
/// Only debug SPIR-V compiles are touched; release compiles, DXIL and preprocessing keep their
/// arguments and bytes. The leaf-name load itself still happens (it precedes the read inside
/// DXC), which is <see cref="DxcLeafNameLookup"/>'s concern.</para>
/// </summary>
internal static class DxcDebugSpirvSource
{
    /// <summary>
    /// The main input name passed to DXC for a debug SPIR-V compile. It appears in the
    /// output's <c>OpString</c> for the main file, and in DXC's raw text of a diagnostic located
    /// before the first <c>#line</c> directive (whose <c>File</c> is reported as
    /// <see cref="DefaultInputName"/>).
    /// </summary>
    public const string InputName = "/dev/null/<shadowdusk-in-memory>/hlsl.hlsl";

    /// <summary>
    /// DXC's own input name when none is passed (every other compile). A diagnostic DXC
    /// locates in <see cref="InputName"/> is reported with this name instead, so a debug
    /// compile points at the same file as its release build.
    /// </summary>
    public const string DefaultInputName = "hlsl.hlsl";

    /// <summary>Raised when DXC's debug SPIR-V cannot be walked to drop the file-read text.</summary>
    public const string ErrorCode = "SD0225";

    private const uint MagicNumber = 0x07230203;

    /// <summary>
    /// The object bytes a DXC compile with <paramref name="arguments"/> ships: unchanged unless
    /// the compile is SPIR-V with debug information, which gets <see cref="DropFileReadText"/>.
    /// Fails <see cref="ErrorCode"/> rather than ship text read from the host's disk.
    /// </summary>
    public static Result<byte[], ShaderError> Normalize(IReadOnlyList<string> arguments, byte[] bytes, string? sourceFileName)
    {
        if (!(arguments.Contains("-spirv") && arguments.Contains("-Zi")))
            return Result<byte[], ShaderError>.Ok(bytes);

        if (DropFileReadText(bytes) is { } normalized)
            return Result<byte[], ShaderError>.Ok(normalized);

        return Result<byte[], ShaderError>.Fail(new ShaderError(
            File: sourceFileName ?? "",
            Line: 0,
            Column: 0,
            Code: ErrorCode,
            Message: $"DXC returned a debug SPIR-V module ({bytes.Length} bytes) whose instruction stream ShadowDusk " +
                     "cannot walk, so the OpSource text DXC may have read from files on disk cannot be removed. " +
                     "A ShadowDusk bug if ever seen; compile without debug information meanwhile.",
            Severity: ShaderErrorSeverity.Error));
    }

    private const ushort OpSourceContinued = 2;
    private const ushort OpSource = 3;
    private const ushort OpString = 7;

    /// <summary>
    /// Returns <paramref name="spirv"/> with the source-text operand removed from every
    /// <c>OpSource</c> whose file is not <see cref="InputName"/>, and the
    /// <c>OpSourceContinued</c> instructions that continue such a text dropped. The same array
    /// is returned when there is nothing to remove. Null when the module cannot be walked
    /// (not a little-endian SPIR-V module, or an instruction overruns it).
    /// </summary>
    public static byte[]? DropFileReadText(byte[] spirv)
    {
        if (spirv.Length < 20 || spirv.Length % 4 != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(spirv) != MagicNumber)
        {
            return null;
        }

        int wordCount = spirv.Length / 4;
        var strings = new Dictionary<uint, string>();
        for (int pos = 5; pos < wordCount;)
        {
            if (!TryReadHeader(spirv, pos, wordCount, out ushort opcode, out int length))
                return null;
            if (opcode == OpString && length >= 3)
                strings[Word(spirv, pos + 1)] = ReadLiteral(spirv, pos + 2, pos + length);
            pos += length;
        }

        List<byte>? output = null;
        bool droppingContinuation = false;
        for (int pos = 5; pos < wordCount;)
        {
            TryReadHeader(spirv, pos, wordCount, out ushort opcode, out int length);
            int keep = length;

            if (opcode == OpSourceContinued && droppingContinuation)
            {
                keep = 0;
            }
            else
            {
                droppingContinuation = false;
                // OpSource: word 0, SourceLanguage, Version, [File], [Source...].
                if (opcode == OpSource && length > 4
                    && strings.GetValueOrDefault(Word(spirv, pos + 3)) != InputName)
                {
                    keep = 4;
                    droppingContinuation = true;
                }
            }

            if (keep != length && output is null)
            {
                output = new List<byte>(spirv.Length);
                output.AddRange(spirv.AsSpan(0, pos * 4));
            }

            if (output is not null && keep > 0)
            {
                int start = output.Count;
                output.AddRange(spirv.AsSpan(pos * 4, keep * 4));
                if (keep != length)
                {
                    uint header = ((uint)keep << 16) | opcode;
                    BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(output).Slice(start, 4), header);
                }
            }

            pos += length;
        }

        return output?.ToArray() ?? spirv;
    }

    private static bool TryReadHeader(byte[] spirv, int pos, int wordCount, out ushort opcode, out int length)
    {
        uint header = Word(spirv, pos);
        opcode = (ushort)(header & 0xFFFF);
        length = (int)(header >> 16);
        return length > 0 && pos + length <= wordCount;
    }

    private static uint Word(byte[] spirv, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(index * 4, 4));

    private static string ReadLiteral(byte[] spirv, int firstWord, int endWord)
    {
        ReadOnlySpan<byte> bytes = spirv.AsSpan(firstWord * 4, (endWord - firstWord) * 4);
        int nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? bytes[..nul] : bytes);
    }
}
