#nullable enable

using System.Buffers.Binary;
using System.Diagnostics;
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
///   commit, <c>1.7.2212.40 (e043f4a12)</c>), so a rebuild that reuses the version number
///   from other sources does not pass.</item>
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
    /// <summary>The pinned DXC, as its Windows file version and source commit.</summary>
    internal const string WindowsCompilerVersion = "1.7.2212.40 (e043f4a12)";

    /// <summary>The <c>dxil.dll</c> Vortice.Dxc 3.3.4 ships beside that DXC, as its file version and build tag.</summary>
    internal const string WindowsValidatorVersion =
        "101.7.2212.36 (release/github-release-1.7.2212, ShaderCompiler.dxcbin.0.230301.1+" +
        "a9f555fbc9e2418fd87996a553e75aab2aa1c0cc-101.7.2212.32.github-release-1.7.2212-4-g0e372b8)";

    /// <summary>GNU build id of Vortice.Dxc 3.3.4's <c>runtimes/linux-x64/native/libdxcompiler.so</c>.</summary>
    internal const string LinuxX64CompilerBuildId = "65681bf462e07b533a7c7bfa54bda4e23a6c5d9f";

    /// <summary><c>LC_UUID</c> of our <c>osx-x64</c> <c>libdxcompiler.dylib</c> (release tag <c>native-dxc-1.7.2212.40</c>).</summary>
    internal const string OsxX64CompilerUuid = "1b5513a41646349c89cb814d05326d9a";

    /// <summary><c>LC_UUID</c> of our <c>osx-arm64</c> <c>libdxcompiler.dylib</c> (same release tag).</summary>
    internal const string OsxArm64CompilerUuid = "9e2d66e9c3a934429f46afaae3fdc480";

    /// <summary>
    /// GNU build id of our <c>android-arm64</c> <c>libdxcompiler.so</c>, the one ShadowDusk.HLSL
    /// packs (release tag <c>native-dxc-1.7.2212.40</c>, SHA-256 pinned in <c>tools/restore.*</c>).
    /// Android has no file to read it from (the library is mapped straight out of the APK), so
    /// <see cref="DxcLoader"/> reads it from the MAPPED image instead.
    /// </summary>
    internal const string AndroidArm64CompilerBuildId = "076af3e5babc2ac0c79bed08b4d8d1b1ac338bc7";

    /// <summary>
    /// GNU build id of the <c>android-x64</c> <c>libdxcompiler.so</c> that the x86_64 emulator
    /// lane of <c>validation/AndroidGl</c> bundles (<c>.wasm-build/build-dxc-android.ps1</c> for
    /// x86_64, the same pinned commit; hosted on release tag <c>native-dxc-1.7.2212.40</c> and
    /// SHA-256 pinned in <c>tools/restore.*</c>, issue #304). No package ships it: it is pinned so
    /// the emulator lane (<c>.github/workflows/android-emulator.yml</c>) runs with the identity
    /// check on. A rebuild of that file gets a new build id and an <c>SD0219</c> naming both,
    /// which is the cue to update this line.
    /// </summary>
    internal const string AndroidX64CompilerBuildId = "38487f7242f477f1eefcb58e587a128c2a54906e";

    /// <summary>
    /// The one Vortice.Dxc release ShadowDusk.HLSL runs with: it ships the pinned DXC natives
    /// (Windows, Linux), and ShadowDusk.HLSL is compiled against its managed API. Any other
    /// release ships a different DXC and, measured for 3.8.3, a binary-incompatible API (the first
    /// DXC call fails with <see cref="MissingMethodException"/>). Must equal the exact range in
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
    /// ProductVersion string (<c>1.7.2212.40 (e043f4a12)</c>), the FILEVERSION alone when the
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

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>; false when the file is shorter.</summary>
    private static bool TryReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
