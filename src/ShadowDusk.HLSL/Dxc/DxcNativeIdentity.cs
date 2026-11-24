#nullable enable

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShadowDusk.Core;

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
/// <item>Windows (PE): the four-part FILEVERSION of the version resource plus the build tag
///   that follows it in the ProductVersion string (for <c>dxcompiler.dll</c> the source
///   commit, <c>1.9.0.5402 (0d3ee6b55-dirty)</c>). It tells other DXC versions apart, but not two
///   builds of this commit: Microsoft's build of the tag carries the same stamp as ours, and
///   compiles the corpus to the same bytes.</item>
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
/// The pins change only with the DXC package ShadowDusk.HLSL takes its Linux and macOS natives
/// from (<c>MonoGame.Tool.Dxc</c>) or our own Windows and Android builds
/// (<c>tools/restore.*</c>); <c>DxcPinnedNativeIdentityTests</c> fails with
/// the value to paste here when a shipped file stops matching.
/// </para>
/// </remarks>
internal static class DxcNativeIdentity
{
    /// <summary>The pinned DXC release, as diagnostics name it (Phase 70: what MonoGame 3.8.6 bundles).</summary>
    internal const string PinnedDxcRelease = "v1.9.2607 (0d3ee6b55)";

    /// <summary>The pinned DXC, as its Windows file version and source commit.</summary>
    internal const string WindowsCompilerVersion = "1.9.0.5402 (0d3ee6b55-dirty)";

    /// <summary>The <c>dxil.dll</c> built with it (<c>.github/workflows/dxc-build.yml</c>), as its file version and build tag.</summary>
    internal const string WindowsValidatorVersion = "1.9.0.5402 (0d3ee6b55-dirty)";

    /// <summary>GNU build id of <c>MonoGame.Tool.Dxc</c> 1.9.2607.1's <c>binaries/linux-x64/lib/libdxcompiler.so</c>.</summary>
    internal const string LinuxX64CompilerBuildId = "875c9449d09db6367641dd5cc37cc8c6d47daf7b";

    /// <summary><c>LC_UUID</c> of the x86_64 slice of <c>MonoGame.Tool.Dxc</c> 1.9.2607.1's universal <c>libdxcompiler.dylib</c>.</summary>
    internal const string OsxX64CompilerUuid = "21971f2aab6c3d999955e295b1f1a451";

    /// <summary><c>LC_UUID</c> of the arm64 slice of the same universal file.</summary>
    internal const string OsxArm64CompilerUuid = "71711714044735c8babc650f876515d7";

    /// <summary>
    /// GNU build id of our <c>android-arm64</c> <c>libdxcompiler.so</c>, the one ShadowDusk.HLSL
    /// packs: the 16 KB page aligned, byte-reproducible build of <c>.github/workflows/dxc-android-build.yml</c>
    /// (<c>tools/build-dxc-android.sh</c>, the pinned commit), release tag
    /// <c>native-dxc-1.9.2607</c>, SHA-256 pinned in <c>tools/restore.*</c>.
    /// Android has no file to read it from (the library is mapped straight out of the APK), so
    /// <see cref="DxcLoader"/> reads it from the MAPPED image instead.
    /// </summary>
    internal const string AndroidArm64CompilerBuildId = "e395fd28ed24ab70ba254f653c55a1ef2ac4c3f1";

    /// <summary>
    /// GNU build id of the <c>android-x64</c> <c>libdxcompiler.so</c> that the x86_64 emulator
    /// lane of <c>validation/AndroidGl</c> bundles (the same CI build as android-arm64, release
    /// tag <c>native-dxc-1.9.2607</c>, SHA-256 pinned in <c>tools/restore.*</c>).
    /// No package ships it: it is pinned so
    /// the emulator lane (<c>.github/workflows/android-emulator.yml</c>) runs with the identity
    /// check on. A rebuild of that file gets a new build id and an <c>SD0219</c> naming both,
    /// which is the cue to update this line.
    /// </summary>
    internal const string AndroidX64CompilerBuildId = "8587fa6685f4e752e4ea4fad9484e1a6e6c35e94";

    /// <summary>
    /// The one Vortice.Dxc release ShadowDusk.HLSL runs with: ShadowDusk.HLSL is compiled against
    /// its managed API and takes none of its natives. Another release has, measured for 3.8.3, a
    /// binary-incompatible API (the first DXC call fails with
    /// <see cref="MissingMethodException"/>). Must equal the exact range in
    /// <c>Directory.Packages.props</c> and the pin in <c>buildTransitive/ShadowDusk.HLSL.targets</c>
    /// (<c>VorticeDxcPinTests</c> fails when they drift).
    /// </summary>
    internal static readonly Version PinnedVorticeDxcVersion = new(3, 3, 4);

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
        ("android-arm64", DxcNativeKind.Compiler) => AndroidArm64CompilerBuildId,
        ("android-x64", DxcNativeKind.Compiler) => AndroidX64CompilerBuildId,
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
    /// A PE file's four-part FILEVERSION followed by the parenthesized build tag of its
    /// ProductVersion string (<c>1.9.0.5402 (0d3ee6b55-dirty)</c>), the FILEVERSION alone when the
    /// ProductVersion carries no tag, or <c>null</c> off Windows (only Windows can read a native
    /// PE's resources, and only Windows loads one) or when the file has no version resource.
    /// </summary>
    private static string? ReadWindowsFileVersion(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        return info.FileVersion is null
            ? null
            : WindowsIdentity(
                $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}",
                info.ProductVersion);
    }

    /// <summary>
    /// <paramref name="fileVersion"/> plus the first parenthesized part of
    /// <paramref name="productVersion"/>, as DXC's build stamps it. Pure, for the unit tests.
    /// </summary>
    internal static string WindowsIdentity(string fileVersion, string? productVersion)
    {
        int open = productVersion?.IndexOf('(') ?? -1;
        int close = open < 0 ? -1 : productVersion!.IndexOf(')', open + 1);
        return close > open + 1
            ? $"{fileVersion} ({productVersion![(open + 1)..close].Trim()})"
            : fileVersion;
    }

    /// <summary>
    /// The GNU build id (<c>NT_GNU_BUILD_ID</c>) of a 64-bit little-endian ELF file as lowercase
    /// hex, or <c>null</c>. Reads only the header, the program headers and the note segments.
    /// </summary>
    internal static string? ReadElfBuildId(Stream stream)
    {
        const int headerSize = 64;
        const uint programHeaderNote = 4;   // PT_NOTE
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
            if (FindGnuBuildId(notes, alignment == 8 ? 8 : 4) is { } buildId)
                return buildId;
        }

        return null;
    }

    /// <summary>
    /// The GNU build id in one ELF note segment (<c>PT_NOTE</c>) as lowercase hex, or
    /// <c>null</c>. Shared by the file reader above and the mapped-image reader
    /// (<see cref="ElfImages"/>, where the implementation now lives), which hands it the segment
    /// as the dynamic linker mapped it.
    /// </summary>
    internal static string? FindGnuBuildId(ReadOnlySpan<byte> notes, int align) =>
        ElfImages.FindGnuBuildId(notes, align);

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

    /// <summary>
    /// False when the PE file at <paramref name="path"/> cannot run in a process of
    /// <paramref name="architecture"/>, read without loading it. The version stamp cannot tell:
    /// win-x64 and win-arm64 carry the same one. And the loader's own failure cannot either:
    /// Microsoft's "arm64" v1.9.2607 DLLs are ARM64EC, whose header says x64, so an x64 process
    /// maps them and crashes on the first call (measured, DXC issue #8934). An unreadable file
    /// answers true: it is the load that reports it.
    /// </summary>
    internal static bool IsPeForArchitecture(string path, Architecture architecture)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return IsPeForArchitecture(stream, architecture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The check of <see cref="IsPeForArchitecture(string, Architecture)"/>, over an open image.</summary>
    internal static bool IsPeForArchitecture(Stream stream, Architecture architecture)
    {
        const ushort MachineX64 = 0x8664, MachineArm64 = 0xAA64, Pe32Plus = 0x20B;
        const int LoadConfigDirectory = 112 + (10 * 8), ChpeMetadataPointer = 0xC8, SectionSize = 40;

        ushort expected = architecture switch
        {
            Architecture.X64 => MachineX64,
            Architecture.Arm64 => MachineArm64,
            _ => (ushort)0,
        };
        Span<byte> word = stackalloc byte[4];
        if (expected == 0 || !TryReadAt(stream, 0x3C, word))
            return true;

        long pe = BinaryPrimitives.ReadUInt32LittleEndian(word);
        Span<byte> fileHeader = stackalloc byte[24];
        if (!TryReadAt(stream, pe, fileHeader))
            return true;

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[4..]);
        if (machine != expected)
            return false;
        if (architecture != Architecture.X64)
            return true;

        // An x64 header with CHPE metadata in its load configuration is ARM64EC.
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[6..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader[20..]);
        long optional = pe + 24;
        Span<byte> magic = stackalloc byte[2];
        Span<byte> directory = stackalloc byte[8];
        if (optionalSize < LoadConfigDirectory + 8
            || !TryReadAt(stream, optional, magic)
            || BinaryPrimitives.ReadUInt16LittleEndian(magic) != Pe32Plus
            || !TryReadAt(stream, optional + LoadConfigDirectory, directory))
        {
            return true;
        }

        uint loadConfigRva = BinaryPrimitives.ReadUInt32LittleEndian(directory);
        if (loadConfigRva == 0)
            return true;

        Span<byte> section = stackalloc byte[SectionSize];
        Span<byte> pointer = stackalloc byte[8];
        for (int i = 0; i < sections; i++)
        {
            if (!TryReadAt(stream, optional + optionalSize + ((long)i * SectionSize), section))
                return true;

            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if (loadConfigRva < virtualAddress || loadConfigRva - virtualAddress >= Math.Max(virtualSize, rawSize))
                continue;

            long loadConfig = rawPointer + (long)(loadConfigRva - virtualAddress);
            return !TryReadAt(stream, loadConfig, word)
                || BinaryPrimitives.ReadUInt32LittleEndian(word) < ChpeMetadataPointer + 8
                || !TryReadAt(stream, loadConfig + ChpeMetadataPointer, pointer)
                || BinaryPrimitives.ReadUInt64LittleEndian(pointer) == 0;
        }

        return true;
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; false when the file is shorter.</summary>
    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
