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
    [InlineData("win-x64", false, "1.9.0.5402 (0d3ee6b55-dirty)")]
    [InlineData("win-arm64", false, "1.9.0.5402 (0d3ee6b55-dirty)")]
    [InlineData("win-x64", true, DxcNativeIdentity.WindowsValidatorVersion)]
    [InlineData("win-arm64", true, DxcNativeIdentity.WindowsValidatorVersion)]
    [InlineData("linux-x64", false, "875c9449d09db6367641dd5cc37cc8c6d47daf7b")]
    [InlineData("osx-x64", false, "21971f2aab6c3d999955e295b1f1a451")]
    [InlineData("osx-arm64", false, "71711714044735c8babc650f876515d7")]
    [InlineData("android-arm64", false, "5882f6181b9e7149c5c5c4dbb2a88ed4e78d3223")]
    [InlineData("android-x64", false, "225337e5e08f3850fa4299f5499ae303f636aea0")]
    public void Expected_IsThePinnedBuildOfEachBundledRid(string rid, bool validator, string expected)
    {
        DxcNativeIdentity.Expected(rid, Kind(validator)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("linux-arm64", false)]
    [InlineData("win-x86", false)]
    [InlineData("linux-x64", true)]
    [InlineData("osx-arm64", true)]
    [InlineData("android-arm64", true)]
    [InlineData("android-arm", false)]
    [InlineData("android-x86", false)]
    public void Expected_IsNullWhereShadowDuskBundlesNoSuchNative(string rid, bool validator)
    {
        // No pin means nothing can match: a DXC found for a RID we do not ship is never ours.
        DxcNativeIdentity.Expected(rid, Kind(validator)).ShouldBeNull();
    }

    [Theory]
    [InlineData("1.9.0.5402", "1.9.0.5402 (0d3ee6b55-dirty)", "1.9.0.5402 (0d3ee6b55-dirty)")]
    [InlineData("1.9.2602.17", "1.9.2602.17 (21d28f727)", "1.9.2602.17 (21d28f727)")]
    [InlineData("1.9.0.5402", "1.9.0.5402 (0123abcde)", "1.9.0.5402 (0123abcde)")]
    [InlineData("1.9.0.5402", "1.9.0.5402", "1.9.0.5402")]
    [InlineData("1.9.0.5402", null, "1.9.0.5402")]
    [InlineData("1.9.0.5402", "1.9.0.5402 ()", "1.9.0.5402")]
    public void WindowsIdentity_IsTheFileVersionPlusTheBuildTag(string fileVersion, string? productVersion, string expected)
    {
        // The commit in the tag is what tells a rebuild of the same version number from other
        // sources apart: "1.9.0.5402 (0123abcde)" is not the pinned build.
        string identity = DxcNativeIdentity.WindowsIdentity(fileVersion, productVersion);

        identity.ShouldBe(expected);
        (identity == DxcNativeIdentity.WindowsCompilerVersion).ShouldBe(expected == "1.9.0.5402 (0d3ee6b55-dirty)");
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
    public void GnuBuildId_IsFoundInANoteSegmentAsMapped()
    {
        const int align = 4;
        // The mapped-image reader (Linux and Android, issue #289) hands over the PT_NOTE segment
        // straight from memory, without the file around it.
        byte[] notes = [.. Note(1, "GNU\0"u8, new byte[16]), .. Note(3, "GNU\0"u8, BuildId)];

        DxcNativeIdentity.FindGnuBuildId(notes, align).ShouldBe("65681bf462e07b533a7c7bfa54bda4e23a6c5d9f");
        DxcNativeIdentity.FindGnuBuildId(notes.AsSpan(0, 28), align).ShouldBeNull();
        DxcNativeIdentity.FindGnuBuildId([], align).ShouldBeNull();
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
    [Theory]
    [InlineData((ushort)0x8664, false, System.Runtime.InteropServices.Architecture.X64, true)]
    [InlineData((ushort)0x8664, true, System.Runtime.InteropServices.Architecture.X64, false)] // ARM64EC
    [InlineData((ushort)0xAA64, false, System.Runtime.InteropServices.Architecture.X64, false)]
    [InlineData((ushort)0xAA64, false, System.Runtime.InteropServices.Architecture.Arm64, true)]
    [InlineData((ushort)0xAA64, true, System.Runtime.InteropServices.Architecture.Arm64, true)] // ARM64X
    [InlineData((ushort)0x8664, false, System.Runtime.InteropServices.Architecture.Arm64, false)]
    [InlineData((ushort)0x8664, true, System.Runtime.InteropServices.Architecture.Arm64, false)] // ARM64EC
    public void PeArchitecture_IsReadFromTheHeaderAndTheLoadConfiguration(
        ushort machine, bool chpe, System.Runtime.InteropServices.Architecture process, bool expected)
    {
        using var image = new MemoryStream(Pe(machine, chpe));

        DxcNativeIdentity.IsPeForArchitecture(image, process).ShouldBe(expected);
    }

    /// <summary>A PE32+ image with one section holding a load configuration.</summary>
    private static byte[] Pe(ushort machine, bool chpe)
    {
        const int peOffset = 0x40, optionalSize = 240, sectionRaw = 0x200, sectionRva = 0x1000;
        byte[] image = new byte[0x400];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
        image[peOffset] = (byte)'P';
        image[peOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
        int optional = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(optional + 112 + 80), sectionRva + 0x10);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(optional + 112 + 84), 0x100);
        int section = optional + optionalSize;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(section + 8), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(section + 12), sectionRva);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(section + 16), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(section + 20), sectionRaw);
        int loadConfig = sectionRaw + 0x10;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(loadConfig), 0x100);
        if (chpe)
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(loadConfig + 0xC8), 0x180001000);
        return image;
    }

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
