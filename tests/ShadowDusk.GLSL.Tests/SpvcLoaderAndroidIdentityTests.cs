#nullable enable

using System.Buffers.Binary;
using Shouldly;
using ShadowDusk.Core;
using ShadowDusk.GLSL.Interop;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Android SPIRV-Cross identity (issue #350): the image the linker maps from the APK must carry
/// the pinned GNU build id for its ABI. The decision is pure, so it is asserted here on every
/// host; the build id itself is read from the restored file the package ships.
/// </summary>
public sealed class SpvcLoaderAndroidIdentityTests
{
    private const string Arm64 = "android-arm64";

    [Fact]
    public void ThePinnedBuild_IsAccepted() =>
        SpvcLoader.VerifyAndroidImage(Arm64, new ElfImages.ElfImage(
            "/data/app/base.apk!/lib/arm64-v8a/libspirv-cross.so", SpvcLoader.AndroidBuildIdByRid[Arm64])).ShouldBeNull();

    [Fact]
    public void ADifferentBuild_IsRefused_NamingBoth()
    {
        ShaderError? error = SpvcLoader.VerifyAndroidImage(Arm64, new ElfImages.ElfImage(
            "/data/app/base.apk!/lib/arm64-v8a/libspirv-cross.so", "0123456789abcdef0123456789abcdef01234567"));

        error.ShouldNotBeNull();
        error.Code.ShouldBe(SpvcLoader.LoadErrorCode);
        error.Message.ShouldContain("0123456789abcdef0123456789abcdef01234567", Case.Sensitive);
        error.Message.ShouldContain(SpvcLoader.AndroidBuildIdByRid[Arm64], Case.Sensitive);
    }

    [Fact]
    public void AnImageWithNoBuildId_IsRefused() =>
        SpvcLoader.VerifyAndroidImage(Arm64, new ElfImages.ElfImage("/x/libspirv-cross.so", null))
            .ShouldNotBeNull().Code.ShouldBe(SpvcLoader.LoadErrorCode);

    [Fact]
    public void AnAbiShadowDuskShipsNoSpirvCrossFor_IsRefused() =>
        SpvcLoader.VerifyAndroidImage("android-arm", new ElfImages.ElfImage("/x/libspirv-cross.so", "aa"))
            .ShouldNotBeNull().Message.ShouldContain("ships no SPIRV-Cross for that ABI", Case.Sensitive);

    [Fact]
    public void WhenTheLinkerCannotBeAsked_ThereIsNoFinding() =>
        SpvcLoader.VerifyAndroidImage(Arm64, image: null).ShouldBeNull();

    /// <summary>
    /// The android-arm64 pin is the build id of the file ShadowDusk.GLSL packs (restored by
    /// <c>tools/restore.*</c> and copied beside this assembly). Skipped as passed where it was not
    /// restored; CI restores it.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void TheArm64Pin_IsTheBuildIdOfThePackagedFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, Arm64, "libspirv-cross.so");
        if (!File.Exists(path))
            return;

        ReadBuildId(File.ReadAllBytes(path)).ShouldBe(SpvcLoader.AndroidBuildIdByRid[Arm64], path);
    }

    /// <summary>
    /// The android-x64 pin is the build id of the x86_64 file <c>tools/restore.*</c> restores from
    /// the release (issue #304): the emulator lane bundles exactly that file, so a pin that drifts
    /// from it would refuse the lane's every compile with <c>SD0103</c>. Skipped as passed where it
    /// was not restored; CI's integration job hard-gates its presence.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void TheX64Pin_IsTheBuildIdOfTheRestoredEmulatorFile()
    {
        string path = Path.Combine(RepoRoot(), "tools", "spirv-cross", "android-x64", "libspirv-cross.so");
        if (!File.Exists(path))
            return;

        ReadBuildId(File.ReadAllBytes(path)).ShouldBe(SpvcLoader.AndroidBuildIdByRid["android-x64"], path);
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }

    /// <summary>The GNU build id from an ELF64 little-endian file's PT_NOTE segments.</summary>
    private static string? ReadBuildId(byte[] elf)
    {
        long phoff = BinaryPrimitives.ReadInt64LittleEndian(elf.AsSpan(0x20));
        int phentsize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x36));
        int phnum = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x38));
        for (int i = 0; i < phnum; i++)
        {
            ReadOnlySpan<byte> ph = elf.AsSpan((int)(phoff + i * phentsize));
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) != 4)
                continue;

            long offset = BinaryPrimitives.ReadInt64LittleEndian(ph[8..]);
            long size = BinaryPrimitives.ReadInt64LittleEndian(ph[32..]);
            long align = BinaryPrimitives.ReadInt64LittleEndian(ph[48..]);
            if (ElfImages.FindGnuBuildId(elf.AsSpan((int)offset, (int)size), align == 8 ? 8 : 4) is { } id)
                return id;
        }

        return null;
    }
}
