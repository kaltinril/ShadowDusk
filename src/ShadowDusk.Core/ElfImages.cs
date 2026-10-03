#nullable enable

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShadowDusk.Core;

/// <summary>
/// Linux and Android: which ELF image the dynamic linker really mapped for a loaded library, and
/// its GNU build id, read from the MAPPED note segment (<c>dl_iterate_phdr</c>), never from a
/// file. On Android a library is mapped straight out of the APK and has no file of its own, so
/// this is the only identity there is. Shared by <c>DxcLoader</c> (issue #289) and
/// <c>SpvcLoader</c>'s Android check (issue #350).
/// </summary>
internal static class ElfImages
{
    /// <summary>An ELF image as the dynamic linker mapped it.</summary>
    /// <param name="Path">The path the linker recorded (on Android, inside the APK).</param>
    /// <param name="BuildId">Its GNU build id as lowercase hex, or <c>null</c> if it has none.</param>
    internal readonly record struct ElfImage(string Path, string? BuildId);

    /// <summary>
    /// Linux and Android: the mapped ELF image that defines <paramref name="exportName"/> in
    /// the library <paramref name="handle"/> names, with the build id read from its mapped
    /// note segment (no file access: on Android the library has no file of its own), or
    /// <c>null</c> elsewhere or when the dynamic linker cannot be asked.
    /// </summary>
    internal static ElfImage? ImageOf(IntPtr handle, string exportName)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) || handle == IntPtr.Zero)
            return null;

        return NativeLibrary.TryGetExport(handle, exportName, out IntPtr address)
            ? ImageContaining(address)
            : null;
    }

    /// <summary>
    /// Linux and Android: the mapped ELF image whose loadable segments contain
    /// <paramref name="address"/>, found with <c>dl_iterate_phdr</c>, or <c>null</c>.
    /// </summary>
    internal static unsafe ElfImage? ImageContaining(IntPtr address)
    {
        if (IntPtr.Size != 8 || ResolveDlIteratePhdr() is not { } iterate)
            return null;

        var search = new ElfSearch { Address = (ulong)address };
        GCHandle state = GCHandle.Alloc(search);
        try
        {
            var dlIteratePhdr =
                (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<DlPhdrInfo*, nuint, IntPtr, int>, IntPtr, int>)iterate;
            dlIteratePhdr(&VisitImage, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        return search.Path is null ? null : new ElfImage(search.Path, search.BuildId);
    }

    private static IntPtr? ResolveDlIteratePhdr()
    {
        // The process's global scope first (it holds libc on glibc and bionic alike), then the
        // libraries that define it by name: glibc's libc.so.6, bionic's libdl.so / libc.so.
        if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "dl_iterate_phdr", out IntPtr address))
            return address;

        foreach (string library in new[] { "libc.so.6", "libdl.so", "libc.so" })
        {
            if (NativeLibrary.TryLoad(library, out IntPtr lib)
                && NativeLibrary.TryGetExport(lib, "dl_iterate_phdr", out address))
            {
                return address;
            }
        }

        return null;
    }

    /// <summary><c>dl_iterate_phdr</c>'s callback: stops (returns 1) at the image holding the address.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int VisitImage(DlPhdrInfo* info, nuint size, IntPtr data)
    {
        try
        {
            const uint loadSegment = 1;   // PT_LOAD
            const uint noteSegment = 4;   // PT_NOTE

            var search = (ElfSearch)GCHandle.FromIntPtr(data).Target!;
            bool contains = false;
            for (int i = 0; i < info->Phnum && !contains; i++)
            {
                Elf64Phdr* segment = &info->Phdr[i];
                ulong start = info->Addr + segment->Vaddr;
                contains = segment->Type == loadSegment
                    && search.Address >= start && search.Address - start < segment->Memsz;
            }

            if (!contains)
                return 0;

            search.Path = Marshal.PtrToStringUTF8(info->Name) ?? "";
            for (int i = 0; i < info->Phnum && search.BuildId is null; i++)
            {
                Elf64Phdr* segment = &info->Phdr[i];
                if (segment->Type != noteSegment || segment->Memsz is 0 or > (1 << 20))
                    continue;

                var notes = new ReadOnlySpan<byte>((void*)(info->Addr + segment->Vaddr), (int)segment->Memsz);
                search.BuildId = FindGnuBuildId(notes, segment->Align == 8 ? 8 : 4);
            }

            return 1;
        }
        catch
        {
            // Never let an exception cross the native frame; the caller sees "not found".
            return 1;
        }
    }

    private sealed class ElfSearch
    {
        public ulong Address;
        public string? Path;
        public string? BuildId;
    }

    /// <summary>The leading fields of <c>struct dl_phdr_info</c> (64-bit; glibc and bionic agree).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DlPhdrInfo
    {
        public ulong Addr;
        public IntPtr Name;
        public Elf64Phdr* Phdr;
        public ushort Phnum;
    }

    /// <summary><c>Elf64_Phdr</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Elf64Phdr
    {
        public uint Type;
        public uint Flags;
        public ulong Offset;
        public ulong Vaddr;
        public ulong Paddr;
        public ulong Filesz;
        public ulong Memsz;
        public ulong Align;
    }

    /// <summary>
    /// The GNU build id in one ELF note segment (<c>PT_NOTE</c>) as lowercase hex, or
    /// <c>null</c>. Shared by the file reader in <c>DxcNativeIdentity</c> and the mapped-image
    /// reader above, which hands it the segment as the dynamic linker mapped it.
    /// </summary>
    internal static string? FindGnuBuildId(ReadOnlySpan<byte> notes, int align)
    {
        const uint noteGnuBuildId = 3;      // NT_GNU_BUILD_ID

        for (int at = 0; at + 12 <= notes.Length;)
        {
            int nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes[at..]);
            int descSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes[(at + 4)..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(notes[(at + 8)..]);
            if (nameSize < 0 || descSize < 0)
                break;

            int nameAt = at + 12;
            long descAt = nameAt + AlignUp(nameSize, align);
            long next = descAt + AlignUp(descSize, align);
            if (descAt + descSize > notes.Length)
                break;

            if (type == noteGnuBuildId && nameSize == 4 && descSize > 0
                && notes.Slice(nameAt, 4).SequenceEqual("GNU\0"u8))
            {
                return Convert.ToHexString(notes.Slice((int)descAt, descSize)).ToLowerInvariant();
            }

            if (next <= at || next > notes.Length)
                break;
            at = (int)next;
        }

        return null;
    }

    private static long AlignUp(int value, int alignment) => ((long)value + alignment - 1) & ~((long)alignment - 1);
}
