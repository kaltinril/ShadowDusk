#nullable enable

using System.Buffers.Binary;
using Shouldly;
using Xunit;

namespace ShadowDusk.Slang.Tests;

/// <summary>
/// Issue #237: the macOS slangc this package bundles must declare a deployment target no newer
/// than macOS 11, so it loads on every macOS .NET runs on. Upstream's own macOS builds declare
/// <c>minos 26.0</c>; a re-pin back to them (or a self-build that lost
/// <c>CMAKE_OSX_DEPLOYMENT_TARGET</c>) fails here on every OS, not only on an old Mac.
///
/// <para>Reads the restored <c>tools/slang/osx-*/</c> files directly (every host restores every
/// RID), parsing the Mach-O <c>LC_BUILD_VERSION</c> load command the same way
/// <c>otool -l</c> reports it.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SlangMacOSDeploymentTargetTests
{
    private static readonly Version MaximumMinos = new(11, 0);

    private const uint MachOMagic64 = 0xfeedfacf;
    private const uint LcBuildVersion = 0x32;
    private const uint CpuTypeX86_64 = 0x01000007;
    private const uint CpuTypeArm64 = 0x0100000c;

    [Theory]
    [InlineData("osx-arm64", CpuTypeArm64)]
    [InlineData("osx-x64", CpuTypeX86_64)]
    public void BundledMacOSNatives_TargetMacOS11_AndTheirRidsArchitecture(string rid, uint cpuType)
    {
        string dir = Path.Combine(FindRepoRoot(), "tools", "slang", rid);
        foreach (string file in new[] { SlangToolPath.ExecutableFileName(rid), SlangToolPath.CompilerLibraryFileName(rid) })
        {
            string path = Path.Combine(dir, file);
            File.Exists(path).ShouldBeTrue($"{path} is not restored (run tools/restore.sh)");

            (uint cpu, Version minos) = ReadMachO(File.ReadAllBytes(path));

            cpu.ShouldBe(cpuType, $"{rid}/{file} CPU type");
            minos.ShouldBeLessThanOrEqualTo(MaximumMinos, $"{rid}/{file} LC_BUILD_VERSION minos");
        }
    }

    /// <summary>CPU type and <c>LC_BUILD_VERSION</c> minos of a thin 64-bit Mach-O.</summary>
    private static (uint CpuType, Version Minos) ReadMachO(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        BinaryPrimitives.ReadUInt32LittleEndian(b).ShouldBe(MachOMagic64, "not a thin 64-bit little-endian Mach-O");
        uint cpuType = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(b[16..]);

        int offset = 32; // sizeof(mach_header_64)
        for (uint i = 0; i < ncmds; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(b[offset..]);
            uint cmdSize = BinaryPrimitives.ReadUInt32LittleEndian(b[(offset + 4)..]);
            if (cmd == LcBuildVersion)
            {
                // build_version_command: cmd, cmdsize, platform, minos (X.Y.Z as xxxx.yy.zz nibbles), ...
                uint minos = BinaryPrimitives.ReadUInt32LittleEndian(b[(offset + 12)..]);
                return (cpuType, new Version((int)(minos >> 16), (int)((minos >> 8) & 0xff)));
            }
            offset += (int)cmdSize;
        }

        throw new ShouldAssertException("no LC_BUILD_VERSION load command");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root (ShadowDusk.slnx).");
    }
}
