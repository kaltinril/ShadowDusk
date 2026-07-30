#nullable enable

using System.Runtime.InteropServices;
using Shouldly;
using ShadowDusk.GLSL.Interop;
using Xunit;

namespace ShadowDusk.GLSL.Tests;

/// <summary>
/// Pure tests for <see cref="SpvcLoader.MapRid"/> — the RID the SPIRV-Cross resolver labels
/// its <c>runtimes/&lt;rid&gt;/native</c> probe with. No native library or disk access; the
/// mapping is pure, so the desktop contract AND the Phase-50 android-* RIDs are asserted
/// directly (regardless of the OS the test runs on). The android RIDs only label the (absent
/// on Android) path probe — the real load is the bare-SONAME fallback from the APK — but the
/// labels must still be the canonical .NET RIDs so packing lands the .so in the right place.
/// </summary>
public sealed class SpvcLoaderTests
{
    [Theory]
    // Windows, per process arch (an x64 native can never load into an arm64 process,
    // so pointing arm64 at win-x64 was a guaranteed-dead probe — bug-hunt 2026-07-27 N18).
    [InlineData(true, false, false, Architecture.X64, "win-x64")]
    [InlineData(true, false, false, Architecture.Arm64, "win-arm64")]
    // macOS, per process arch (ProcessArchitecture under Rosetta 2, not OSArchitecture).
    [InlineData(false, true, false, Architecture.Arm64, "osx-arm64")]
    [InlineData(false, true, false, Architecture.X64, "osx-x64")]
    // Android (Phase 50): arm64-v8a is primary; x86_64 emulator + armeabi-v7a are stretch.
    [InlineData(false, false, true, Architecture.Arm64, "android-arm64")]
    [InlineData(false, false, true, Architecture.X64, "android-x64")]
    [InlineData(false, false, true, Architecture.Arm, "android-arm")]
    // Linux is the default fallthrough; arm64 gets its own RID on every OS (bug-hunt
    // 2026-07-27 N18: Silk.NET ships win-arm64/linux-arm64 natives, and collapsing to
    // x64 probed a binary an arm64 process can never load).
    [InlineData(false, false, false, Architecture.X64, "linux-x64")]
    [InlineData(false, false, false, Architecture.Arm64, "linux-arm64")]
    public void MapRid_ReturnsCanonicalRid(
        bool isWindows, bool isOsx, bool isAndroid, Architecture arch, string expected)
    {
        SpvcLoader.MapRid(isWindows, isOsx, isAndroid, arch).ShouldBe(expected);
    }

    [Fact]
    public void MapRid_PrioritisesWindowsThenOsxThenAndroid()
    {
        // Defense-in-depth: if more than one OS flag were ever set, the order is
        // Windows > macOS > Android > Linux. The production caller never sets two, but the
        // switch ordering is load-bearing, so pin it.
        SpvcLoader.MapRid(isWindows: true, isOsx: true, isAndroid: true, Architecture.Arm64)
            .ShouldBe("win-arm64");
        SpvcLoader.MapRid(isWindows: false, isOsx: true, isAndroid: true, Architecture.Arm64)
            .ShouldBe("osx-arm64");
    }
}
