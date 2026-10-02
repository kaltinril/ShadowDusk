#nullable enable

using System.Buffers.Binary;
using Shouldly;
using ShadowDusk.HLSL.Dxc;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Dxc;

/// <summary>
/// Pure tests for <see cref="DxcNativeIdentity"/>'s readers, over images built in memory (no
/// disk, no native). The same readers run against the real shipped natives in
/// <c>DxcPinnedNativeIdentityTests</c> (integration).
/// </summary>
public sealed class DxcNativeIdentityTests
{
    private static readonly byte[] BuildId =
        [0x65, 0x68, 0x1b, 0xf4, 0x62, 0xe0, 0x7b, 0x53, 0x3a, 0x7c, 0x7b, 0xfa, 0x54, 0xbd, 0xa4, 0xe2, 0x3a, 0x6c, 0x5d, 0x9f];

    private static readonly byte[] UuidA = Convert.FromHexString("9e2d66e9c3a934429f46afaae3fdc480");
    private static readonly byte[] UuidB = Convert.FromHexString("1b5513a41646349c89cb814d05326d9a");

    [Theory]
    [InlineData("win-x64", false, "1.7.2212.40 (e043f4a12)")]
    [InlineData("win-arm64", false, "1.7.2212.40 (e043f4a12)")]
    [InlineData("win-x64", true, DxcNativeIdentity.WindowsValidatorVersion)]
    [InlineData("win-arm64", true, DxcNativeIdentity.WindowsValidatorVersion)]
    [InlineData("linux-x64", false, "65681bf462e07b533a7c7bfa54bda4e23a6c5d9f")]
    [InlineData("osx-x64", false, "1b5513a41646349c89cb814d05326d9a")]
    [InlineData("osx-arm64", false, "9e2d66e9c3a934429f46afaae3fdc480")]
    public void Expected_IsThePinnedBuildOfEachBundledRid(string rid, bool validator, string expected)
    {
        DxcNativeIdentity.Expected(rid, Kind(validator)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("linux-arm64", false)]
    [InlineData("win-x86", false)]
    [InlineData("linux-x64", true)]
    [InlineData("osx-arm64", true)]
    public void Expected_IsNullWhereShadowDuskBundlesNoSuchNative(string rid, bool validator)
    {
        // No pin means nothing can match: a DXC found for a RID we do not ship is never ours.
        DxcNativeIdentity.Expected(rid, Kind(validator)).ShouldBeNull();
    }

    [Theory]
    [InlineData("1.7.2212.40", "1.7.2212.40 (e043f4a12)", "1.7.2212.40 (e043f4a12)")]
    [InlineData("1.9.2602.17", "1.9.2602.17 (21d28f727)", "1.9.2602.17 (21d28f727)")]
    [InlineData("1.7.2212.40", "1.7.2212.40 (0123abcde)", "1.7.2212.40 (0123abcde)")]
    [InlineData("1.7.2212.40", "1.7.2212.40", "1.7.2212.40")]
    [InlineData("1.7.2212.40", null, "1.7.2212.40")]
    [InlineData("1.7.2212.40", "1.7.2212.40 ()", "1.7.2212.40")]
    public void WindowsIdentity_IsTheFileVersionPlusTheBuildTag(string fileVersion, string? productVersion, string expected)
    {
        // The commit in the tag is what tells a rebuild of the same version number from other
        // sources apart: "1.7.2212.40 (0123abcde)" is not the pinned build.
        string identity = DxcNativeIdentity.WindowsIdentity(fileVersion, productVersion);

        identity.ShouldBe(expected);
        (identity == DxcNativeIdentity.WindowsCompilerVersion).ShouldBe(expected == "1.7.2212.40 (e043f4a12)");
    }

    private static DxcNativeKind Kind(bool validator) =>
        validator ? DxcNativeKind.Validator : DxcNativeKind.Compiler;

    [Fact]
    public void ElfBuildId_IsReadFromTheGnuNote()
    {
        using var elf = new MemoryStream(Elf(Note(3, "GNU\0"u8, BuildId)));

        DxcNativeIdentity.ReadElfBuildId(elf).ShouldBe("65681bf462e07b533a7c7bfa54bda4e23a6c5d9f");
    }

    [Fact]
    public void ElfBuildId_SkipsOtherNotesInTheSameSegment()
    {
        // Real libraries carry more than one note (ABI tag, Android ident) in one PT_NOTE.
        byte[] notes = [.. Note(1, "GNU\0"u8, new byte[16]), .. Note(1, "Android\0"u8, new byte[7]), .. Note(3, "GNU\0"u8, BuildId)];
        using var elf = new MemoryStream(Elf(notes));

        DxcNativeIdentity.ReadElfBuildId(elf).ShouldBe("65681bf462e07b533a7c7bfa54bda4e23a6c5d9f");
    }

    [Fact]
    public void ElfBuildId_IsNullWithoutABuildIdNote()
    {
        using var elf = new MemoryStream(Elf(Note(1, "GNU\0"u8, new byte[16])));

        DxcNativeIdentity.ReadElfBuildId(elf).ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(40)]
    [InlineData(70)]
    public void ElfBuildId_IsNullForATruncatedFile_NeverAnException(int length)
    {
        byte[] full = Elf(Note(3, "GNU\0"u8, BuildId));
        using var elf = new MemoryStream(full[..length]);

        DxcNativeIdentity.ReadElfBuildId(elf).ShouldBeNull();
    }

    [Fact]
    public void ElfBuildId_IsNullWhenANoteClaimsMoreBytesThanTheSegmentHolds()
    {
        byte[] note = Note(3, "GNU\0"u8, BuildId);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), 0x7FFF_FFF0); // descsz
        using var elf = new MemoryStream(Elf(note));

        DxcNativeIdentity.ReadElfBuildId(elf).ShouldBeNull();
    }

    [Fact]
    public void MachOUuid_IsReadFromAThinImage()
    {
        using var image = new MemoryStream(MachO(UuidA));

        DxcNativeIdentity.ReadMachOUuids(image).ShouldBe(new[] { "9e2d66e9c3a934429f46afaae3fdc480" });
    }

    [Fact]
    public void MachOUuid_OfAUniversalFile_IsOnePerSlice()
    {
        // A consumer can lipo our two per-arch dylibs into one universal file for a universal
        // app bundle: it is still the pinned build, for whichever slice the process loads.
        using var image = new MemoryStream(Fat(MachO(UuidB), MachO(UuidA)));

        DxcNativeIdentity.ReadMachOUuids(image).ShouldBe(new[] {
            "1b5513a41646349c89cb814d05326d9a",
            "9e2d66e9c3a934429f46afaae3fdc480"});
    }

    [Fact]
    public void MachOUuid_IsEmptyForAnImageWithoutOne()
    {
        using var image = new MemoryStream(MachO(uuid: null));

        DxcNativeIdentity.ReadMachOUuids(image).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(31)]
    [InlineData(50)]
    public void MachOUuid_IsEmptyForATruncatedFile_NeverAnException(int length)
    {
        using var image = new MemoryStream(MachO(UuidA)[..length]);

        DxcNativeIdentity.ReadMachOUuids(image).ShouldBeEmpty();
    }

    [Fact]
    public void MachOUuid_IsEmptyForSomethingThatIsNotMachO()
    {
        using var image = new MemoryStream(Elf(Note(3, "GNU\0"u8, BuildId)));

        DxcNativeIdentity.ReadMachOUuids(image).ShouldBeEmpty();
    }

    /// <summary>One ELF note: namesz, descsz, type, then name and desc, each padded to 4 bytes.</summary>
    private static byte[] Note(uint type, ReadOnlySpan<byte> name, ReadOnlySpan<byte> desc)
    {
        int Pad(int n) => (n + 3) & ~3;
        byte[] note = new byte[12 + Pad(name.Length) + Pad(desc.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(note, (uint)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(4), (uint)desc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(note.AsSpan(8), type);
        name.CopyTo(note.AsSpan(12));
        desc.CopyTo(note.AsSpan(12 + Pad(name.Length)));
        return note;
    }

    /// <summary>
    /// A minimal ELF64 little-endian image: header, a PT_LOAD and a PT_NOTE program header,
    /// then the note segment.
    /// </summary>
    private static byte[] Elf(byte[] notes)
    {
        const int headerSize = 64, entrySize = 56, entries = 2;
        int notesAt = headerSize + (entries * entrySize);
        byte[] image = new byte[notesAt + notes.Length];

        image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
        image[4] = 2; // ELFCLASS64
        image[5] = 1; // ELFDATA2LSB
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), headerSize); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), entrySize);  // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), entries);    // e_phnum

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerSize), 1);    // PT_LOAD

        Span<byte> note = image.AsSpan(headerSize + entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(note, 4);                            // PT_NOTE
        BinaryPrimitives.WriteUInt64LittleEndian(note[8..], (ulong)notesAt);          // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(note[32..], (ulong)notes.Length);    // p_filesz
        BinaryPrimitives.WriteUInt64LittleEndian(note[48..], 4);                      // p_align

        notes.CopyTo(image, notesAt);
        return image;
    }

    /// <summary>
    /// A minimal 64-bit Mach-O image: header, an LC_ID_DYLIB-sized filler command, then
    /// (optionally) LC_UUID.
    /// </summary>
    private static byte[] MachO(byte[]? uuid)
    {
        const int headerSize = 32, fillerSize = 48, uuidSize = 24;
        byte[] image = new byte[headerSize + fillerSize + (uuid is null ? 0 : uuidSize)];

        BinaryPrimitives.WriteUInt32LittleEndian(image, 0xFEEDFACF);                          // MH_MAGIC_64
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), uuid is null ? 1u : 2u);  // ncmds

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerSize), 0xD);             // LC_ID_DYLIB
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerSize + 4), fillerSize);

        if (uuid is not null)
        {
            int at = headerSize + fillerSize;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), 0x1B);                // LC_UUID
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 4), uuidSize);
            uuid.CopyTo(image, at + 8);
        }

        return image;
    }

    /// <summary>A universal (fat) file wrapping the given thin images; big-endian header.</summary>
    private static byte[] Fat(params byte[][] slices)
    {
        const int recordSize = 20;
        int at = 8 + (slices.Length * recordSize);
        byte[] image = new byte[at + slices.Sum(s => s.Length)];

        BinaryPrimitives.WriteUInt32BigEndian(image, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4), (uint)slices.Length);
        for (int i = 0; i < slices.Length; i++)
        {
            Span<byte> record = image.AsSpan(8 + (i * recordSize));
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)at);                   // offset
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)slices[i].Length);    // size
            slices[i].CopyTo(image, at);
            at += slices[i].Length;
        }

        return image;
    }
}
