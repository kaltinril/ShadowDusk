#nullable enable

using System.Buffers.Binary;
using System.Diagnostics;

namespace ShadowDusk.HLSL.Dxc;

/// <summary>Which of the two DXC natives a file is expected to be.</summary>
internal enum DxcNativeKind
{
    /// <summary><c>dxcompiler.dll</c> / <c>libdxcompiler.so</c> / <c>libdxcompiler.dylib</c>.</summary>
    Compiler,

    /// <summary><c>dxil.dll</c>, the Windows DXIL validator and signer.</summary>
    Validator,
}

/// <summary>
/// Decides whether a DXC native on disk is the build ShadowDusk pins, before
/// <see cref="DxcLoader"/> loads it (issue #270).
/// </summary>
/// <remarks>
/// <para>
/// A file NAME proves nothing: MonoGame's own tools ship a <c>dxcompiler.dll</c> 1.8, the
/// Windows SDK ships a <c>dxil.dll</c> + <c>dxcompiler.dll</c> 1.8 pair whose DXC has no
/// SPIR-V backend, and the Vulkan SDK ships a 1.7.0 one. Any of them in a probed directory used
/// to be loaded and compiled with, silently. A different DXC is a substitute compiler, so the
/// loader now asks the file which build it is.
/// </para>
/// <para>
/// The identity is the one the build tool stamped into the file, read without loading it:
/// </para>
/// <list type="bullet">
/// <item>Windows (PE): the four-part FILEVERSION of the version resource.</item>
/// <item>Linux (ELF): the GNU build id note.</item>
/// <item>macOS (Mach-O): the <c>LC_UUID</c> load command; a universal (fat) file is accepted
///   when any slice carries the pinned id.</item>
/// </list>
/// <para>
/// Deliberately NOT a hash of the whole file. A consumer who ships a macOS app must code-sign
/// every dylib in it, which rewrites the file; Authenticode re-signing and <c>strip</c> in a
/// Linux package build do the same. None of those changes which compiler it is, and all of
/// them would turn a content hash into a refusal the consumer cannot fix. The stamped build id
/// survives all three and still differs for every other DXC build.
/// </para>
/// <para>
/// The pins change only with the Vortice.Dxc package version (Windows, Linux) or our own
/// macOS DXC build (<c>tools/restore.*</c>); <c>DxcPinnedNativeIdentityTests</c> fails with
/// the value to paste here when a shipped file stops matching.
/// </para>
/// </remarks>
internal static class DxcNativeIdentity
{
    /// <summary>The pinned DXC, as its Windows file version (commit <c>e043f4a1</c>).</summary>
    internal const string WindowsCompilerVersion = "1.7.2212.40";

    /// <summary>The <c>dxil.dll</c> Vortice.Dxc 3.3.4 ships beside that DXC.</summary>
    internal const string WindowsValidatorVersion = "101.7.2212.36";

    /// <summary>GNU build id of Vortice.Dxc 3.3.4's <c>runtimes/linux-x64/native/libdxcompiler.so</c>.</summary>
    internal const string LinuxX64CompilerBuildId = "65681bf462e07b533a7c7bfa54bda4e23a6c5d9f";

    /// <summary><c>LC_UUID</c> of our <c>osx-x64</c> <c>libdxcompiler.dylib</c> (release tag <c>native-dxc-1.7.2212.40</c>).</summary>
    internal const string OsxX64CompilerUuid = "1b5513a41646349c89cb814d05326d9a";

    /// <summary><c>LC_UUID</c> of our <c>osx-arm64</c> <c>libdxcompiler.dylib</c> (same release tag).</summary>
    internal const string OsxArm64CompilerUuid = "9e2d66e9c3a934429f46afaae3fdc480";

    /// <summary>
    /// The pinned identity for <paramref name="rid"/>, or <c>null</c> when ShadowDusk bundles no
    /// such native for it (no DXC at all for that RID, or no validator off Windows).
    /// </summary>
    internal static string? Expected(string rid, DxcNativeKind kind) => (rid, kind) switch
    {
        ("win-x64" or "win-arm64", DxcNativeKind.Compiler) => WindowsCompilerVersion,
        ("win-x64" or "win-arm64", DxcNativeKind.Validator) => WindowsValidatorVersion,
        ("linux-x64", DxcNativeKind.Compiler) => LinuxX64CompilerBuildId,
        ("osx-x64", DxcNativeKind.Compiler) => OsxX64CompilerUuid,
        ("osx-arm64", DxcNativeKind.Compiler) => OsxArm64CompilerUuid,
        _ => null,
    };

    /// <summary>True when the file at <paramref name="path"/> carries <paramref name="expected"/>.</summary>
    internal static bool Matches(string path, string? expected) =>
        expected is not null && Read(path).Contains(expected, StringComparer.Ordinal);

    /// <summary>What <see cref="Read(string)"/> found, for a diagnostic.</summary>
    internal static string Describe(string path)
    {
        IReadOnlyList<string> found = Read(path);
        return found.Count == 0 ? "no readable build identity" : string.Join(" / ", found);
    }

    /// <summary>
    /// The build identities stamped into the native at <paramref name="path"/>: one for a PE or
    /// ELF file, one per slice for a Mach-O file. Empty when the file is unreadable, is none of
    /// those formats, or carries no identity: "not provably ours", never an exception.
    /// </summary>
    internal static IReadOnlyList<string> Read(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (!TryReadAt(stream, 0, magic))
                return [];

            if (magic[0] == (byte)'M' && magic[1] == (byte)'Z')
                return ReadWindowsFileVersion(path) is { } version ? [version] : [];

            if (magic.SequenceEqual(ElfMagic))
                return ReadElfBuildId(stream) is { } buildId ? [buildId] : [];

            return ReadMachOUuids(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static ReadOnlySpan<byte> ElfMagic => [0x7F, (byte)'E', (byte)'L', (byte)'F'];

    /// <summary>
    /// The four-part FILEVERSION of a PE file's version resource (<c>1.7.2212.40</c>), or
    /// <c>null</c> off Windows (only Windows can read a native PE's resources, and only Windows
    /// loads one) or when the file has no version resource.
    /// </summary>
    private static string? ReadWindowsFileVersion(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        return info.FileVersion is null
            ? null
            : $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
    }

    /// <summary>
    /// The GNU build id (<c>NT_GNU_BUILD_ID</c>) of a 64-bit little-endian ELF file as lowercase
    /// hex, or <c>null</c>. Reads only the header, the program headers and the note segments.
    /// </summary>
    internal static string? ReadElfBuildId(Stream stream)
    {
        const int headerSize = 64;
        const uint programHeaderNote = 4;   // PT_NOTE
        const uint noteGnuBuildId = 3;      // NT_GNU_BUILD_ID
        const int maxNoteSegment = 1 << 20;

        Span<byte> header = stackalloc byte[headerSize];
        if (!TryReadAt(stream, 0, header) || !header[..4].SequenceEqual(ElfMagic))
            return null;
        if (header[4] != 2 || header[5] != 1) // ELFCLASS64, ELFDATA2LSB: every RID we ship DXC for
            return null;

        long programHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]);
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(header[0x36..]);
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(header[0x38..]);
        if (entrySize < 56)
            return null;

        Span<byte> entry = stackalloc byte[56];
        for (int i = 0; i < entryCount; i++)
        {
            if (!TryReadAt(stream, programHeaderOffset + ((long)i * entrySize), entry))
                return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != programHeaderNote)
                continue;

            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
            ulong alignment = BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]);
            if (size is 0 or > maxNoteSegment)
                continue;

            byte[] notes = new byte[(int)size];
            if (!TryReadAt(stream, offset, notes))
                continue;

            // Notes are 4-byte aligned unless the segment says 8 (the .note.gnu.property form).
            int align = alignment == 8 ? 8 : 4;
            for (int at = 0; at + 12 <= notes.Length;)
            {
                int nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(at));
                int descSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(at + 4));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(notes.AsSpan(at + 8));
                if (nameSize < 0 || descSize < 0)
                    break;

                int nameAt = at + 12;
                long descAt = nameAt + AlignUp(nameSize, align);
                long next = descAt + AlignUp(descSize, align);
                if (descAt + descSize > notes.Length)
                    break;

                if (type == noteGnuBuildId && nameSize == 4 && descSize > 0
                    && notes.AsSpan(nameAt, 4).SequenceEqual("GNU\0"u8))
                {
                    return Convert.ToHexString(notes, (int)descAt, descSize).ToLowerInvariant();
                }

                if (next <= at || next > notes.Length)
                    break;
                at = (int)next;
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>LC_UUID</c> of every 64-bit slice of a Mach-O file (one for a thin file, one per
    /// architecture for a universal one) as lowercase hex. Empty for anything else.
    /// </summary>
    internal static IReadOnlyList<string> ReadMachOUuids(Stream stream)
    {
        const uint machO64 = 0xFEEDFACF;      // MH_MAGIC_64, little-endian on disk
        const uint fat = 0xCAFEBABE;          // FAT_MAGIC, big-endian on disk
        const uint fat64 = 0xCAFEBABF;        // FAT_MAGIC_64
        const int maxSlices = 16;

        Span<byte> head = stackalloc byte[8];
        if (!TryReadAt(stream, 0, head))
            return [];

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) == machO64)
            return ReadMachOSliceUuid(stream, 0) is { } uuid ? [uuid] : [];

        uint fatMagic = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (fatMagic is not (fat or fat64))
            return [];

        // fat_header { magic, nfat_arch }, then nfat_arch records, all big-endian:
        // fat_arch    { cputype, cpusubtype, offset(4), size(4), align }           = 20 bytes
        // fat_arch_64 { cputype, cpusubtype, offset(8), size(8), align, reserved } = 32 bytes
        uint sliceCount = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        if (sliceCount > maxSlices)
            return [];

        int recordSize = fatMagic == fat64 ? 32 : 20;
        var uuids = new List<string>();
        Span<byte> record = stackalloc byte[32];
        for (int i = 0; i < sliceCount; i++)
        {
            if (!TryReadAt(stream, 8 + ((long)i * recordSize), record[..recordSize]))
                break;

            long sliceOffset = fatMagic == fat64
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(record[8..])
                : BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            if (ReadMachOSliceUuid(stream, sliceOffset) is { } uuid)
                uuids.Add(uuid);
        }

        return uuids;
    }

    private static string? ReadMachOSliceUuid(Stream stream, long sliceOffset)
    {
        const uint machO64 = 0xFEEDFACF;
        const uint loadCommandUuid = 0x1B;    // LC_UUID
        const int headerSize = 32;            // mach_header_64
        const int maxCommands = 4096;

        Span<byte> header = stackalloc byte[headerSize];
        if (!TryReadAt(stream, sliceOffset, header)
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != machO64)
        {
            return null;
        }

        uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (commandCount > maxCommands)
            return null;

        long at = sliceOffset + headerSize;
        Span<byte> command = stackalloc byte[24]; // load_command { cmd, cmdsize } + a 16-byte uuid
        for (int i = 0; i < commandCount; i++)
        {
            if (!TryReadAt(stream, at, command[..8]))
                return null;

            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(command);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (size < 8)
                return null;

            if (cmd == loadCommandUuid && size >= 24)
            {
                return TryReadAt(stream, at, command)
                    ? Convert.ToHexString(command[8..24]).ToLowerInvariant()
                    : null;
            }

            at += size;
        }

        return null;
    }

    private static long AlignUp(int value, int alignment) => ((long)value + alignment - 1) & ~((long)alignment - 1);

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; false when the file is shorter.</summary>
    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
