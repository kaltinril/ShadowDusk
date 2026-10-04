#nullable enable

using Shouldly;
using ShadowDusk.HLSL.Dxc;
using ShadowDusk.Integration.Tests.Tests;
using Xunit;

namespace ShadowDusk.Integration.Tests.Dxc;

/// <summary>
/// The DXC natives this build ships must read as the build <see cref="DxcNativeIdentity"/> pins,
/// with the real readers over the real files. <c>DxcLoader</c> refuses any DXC that does not
/// (issue #270), so if a native is ever re-pinned (a Vortice.Dxc bump, a rebuilt macOS dylib)
/// without updating the pin, EVERY DXC-backed compile on that RID fails with <c>SD0219</c>:
/// this test fails first, on every host, and its message carries the value to pin.
///
/// <para>The ELF and Mach-O readers are plain file parsing, so the Linux and macOS natives are
/// checked from any host (the build copies every RID's natives beside the test assembly). A
/// PE version resource can only be read on Windows.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DxcPinnedNativeIdentityTests
{
    [Fact]
    public void LinuxNative_CarriesThePinnedBuildId()
    {
        AssertPinned(
            Path.Combine(ForeignDxc.NativeDirectory("linux-x64"), "libdxcompiler.so"),
            "linux-x64",
            DxcNativeKind.Compiler);
    }

    [WindowsTheory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void WindowsPair_CarriesThePinnedFileVersions(string rid)
    {
        AssertPinned(Path.Combine(ForeignDxc.NativeDirectory(rid), "dxcompiler.dll"), rid, DxcNativeKind.Compiler);
        AssertPinned(Path.Combine(ForeignDxc.NativeDirectory(rid), "dxil.dll"), rid, DxcNativeKind.Validator);
    }

    [RestoredMacDxcTheory]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void MacDylib_CarriesThePinnedUuid(string rid)
    {
        AssertPinned(Path.Combine(AppContext.BaseDirectory, rid, "libdxcompiler.dylib"), rid, DxcNativeKind.Compiler);
    }

    /// <summary>
    /// The Android DXC builds read as their pins too: android-arm64 is the one ShadowDusk.HLSL
    /// packs, android-x64 the one the emulator lane bundles (issue #304). Android reads the build
    /// id from the mapped image, never from this file, so only this test ties the pin to the
    /// hosted asset before the emulator lane runs.
    /// </summary>
    [RestoredAndroidDxcTheory]
    [InlineData("android-arm64")]
    [InlineData("android-x64")]
    public void AndroidNative_CarriesThePinnedBuildId(string rid)
    {
        AssertPinned(RestoredAndroidDxcTheoryAttribute.PathFor(rid), rid, DxcNativeKind.Compiler);
    }

    [WindowsSdkDxilFact]
    public void WindowsSdkPair_IsNotThePinnedBuild()
    {
        // The reader must tell a real foreign DXC apart, not only recognize ours: the SDK's
        // pair is the one measured to compile DirectX 12 silently when it was loaded.
        string sdk = WindowsDllSearch.FindWindowsSdkBinWithDxil()!;

        DxcNativeIdentity.Matches(
            Path.Combine(sdk, "dxcompiler.dll"),
            DxcNativeIdentity.Expected("win-x64", DxcNativeKind.Compiler)).ShouldBeFalse();
        DxcNativeIdentity.Matches(
            Path.Combine(sdk, "dxil.dll"),
            DxcNativeIdentity.Expected("win-x64", DxcNativeKind.Validator)).ShouldBeFalse();
    }

    [Fact]
    public void MissingOrUnreadableFile_HasNoIdentity_NeverAnException()
    {
        string missing = Path.Combine(Path.GetTempPath(), "sd-no-such-native-" + Guid.NewGuid().ToString("N"));

        DxcNativeIdentity.Read(missing).ShouldBeEmpty();
        DxcNativeIdentity.Matches(missing, DxcNativeIdentity.LinuxX64CompilerBuildId).ShouldBeFalse();
        DxcNativeIdentity.Matches(typeof(DxcPinnedNativeIdentityTests).Assembly.Location, expected: null).ShouldBeFalse();
    }

    private static void AssertPinned(string path, string rid, DxcNativeKind kind)
    {
        File.Exists(path).ShouldBeTrue($"{path} is not restored");

        string? expected = DxcNativeIdentity.Expected(rid, kind);
        expected.ShouldNotBeNull($"no pinned identity for {rid} {kind}");

        DxcNativeIdentity.Read(path).ShouldContain(expected,
            $"{path} is not the build DxcNativeIdentity pins for {rid}, so DxcLoader would refuse " +
            "it and every DXC-backed compile there would fail with SD0219. If the native was " +
            $"re-pinned on purpose, pin its identity ({DxcNativeIdentity.Describe(path)}) in " +
            "DxcNativeIdentity; otherwise the wrong native was restored.");
    }
}

/// <summary>
/// A theory over the restored macOS DXC dylibs (<c>tools/restore.*</c> copies both arches
/// beside the test assembly on every host). Skipped, never passed, where they have not been
/// restored, unless <c>SHADOWDUSK_REQUIRE_DXC</c> is set (CI): then it runs and fails.
/// </summary>
public sealed class RestoredMacDxcTheoryAttribute : TheoryAttribute
{
    public RestoredMacDxcTheoryAttribute()
    {
        bool restored = new[] { "osx-x64", "osx-arm64" }.All(rid =>
            File.Exists(Path.Combine(AppContext.BaseDirectory, rid, "libdxcompiler.dylib")));

        if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                restored,
                Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = DxcTestGate.SkipReason;
        }
    }
}

/// <summary>
/// A theory over the restored Android DXC natives (<c>tools/dxc/android-*/libdxcompiler.so</c>,
/// restored by <c>tools/restore.*</c>; nothing copies them beside the test assembly). Skipped,
/// never passed, where they have not been restored, unless <c>SHADOWDUSK_REQUIRE_DXC</c> is set
/// (CI): then it runs and fails.
/// </summary>
public sealed class RestoredAndroidDxcTheoryAttribute : TheoryAttribute
{
    public RestoredAndroidDxcTheoryAttribute()
    {
        bool restored = new[] { "android-arm64", "android-x64" }.All(rid => File.Exists(PathFor(rid)));

        if (ShadowDusk.Tests.Shared.NativeRequirement.ShouldSkip(
                restored,
                Environment.GetEnvironmentVariable(ShadowDusk.Tests.Shared.NativeRequirement.DxcEnvVar)))
        {
            Skip = "The Android DXC natives are not restored (run tools/restore.*).";
        }
    }

    public static string PathFor(string rid)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ShadowDusk.slnx")))
                return Path.Combine(dir.FullName, "tools", "dxc", rid, "libdxcompiler.so");
        }

        throw new InvalidOperationException("Could not locate the repo root (ShadowDusk.slnx).");
    }
}
