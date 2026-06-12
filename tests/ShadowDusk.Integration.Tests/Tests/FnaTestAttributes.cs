#nullable enable

using System.Runtime.InteropServices;
using ShadowDusk.Tests.Shared;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Skip gate for the FNA fx_2_0 integration tests: every FNA compile goes through the
/// native vkd3d-shader SM1–3 backend, a RESTORED artifact. Since Phase 37 C
/// (2026-06-10) the pinned binaries for all four RIDs are hosted and
/// <c>tools/restore.*</c> downloads them on every CI run, so CI normally HAS the
/// native; only local runs that haven't restored it should ever skip. When the library
/// can't be found these tests skip with a clear reason — UNLESS
/// <c>SHADOWDUSK_REQUIRE_VKD3D</c> is set (CI), in which case they run and fail loudly
/// at the native boundary (SD0211) instead of skipping green (see
/// <see cref="NativeRequirement"/>). The sibling of
/// <c>ShadowDusk.HLSL.Tests.Vkd3d.Vkd3dFactAttribute</c>, but availability-probed rather
/// than OS-gated so a future restored Linux/macOS binary enables them automatically.
/// </summary>
internal static class FnaTestGate
{
    internal const string SkipReason =
        "vkd3d-shader native library not found (restore it via tools/restore — " +
        "see plan/DONE/PHASE-37-cross-platform-native-availability.md, finding C).";

    internal static bool Vkd3dAvailable { get; } = ProbeVkd3d();

    // Mirrors Vkd3dLoader's probe: the build-output copy next to the test binaries,
    // then tools/vkd3d/ walking up from the output directory toward the repo root.
    // On macOS the restored layout is per-arch (osx-x64/ / osx-arm64/ subdirectories
    // — both arches share one dylib file name), so the current process arch's
    // subdirectory is probed before the flat path, like the loader.
    private static bool ProbeVkd3d()
    {
        foreach (string name in CandidateFileNames())
        {
            foreach (string subdir in ProbeSubdirectories())
            {
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, subdir, name)))
                    return true;

                for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "tools", "vkd3d", subdir, name)))
                        return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "libvkd3d-shader-1.dll";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "libvkd3d-shader.1.dylib";
            yield return "libvkd3d-shader.dylib";
        }
        else
        {
            yield return "libvkd3d-shader.so.1";
            yield return "libvkd3d-shader.so";
        }
    }

    private static string[] ProbeSubdirectories()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return [""];
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "osx-arm64" : "osx-x64";
        return [arch, ""];
    }
}

/// <summary>
/// An xUnit fact that skips when the vkd3d-shader native library is absent — unless
/// <c>SHADOWDUSK_REQUIRE_VKD3D</c> is set, in which case the test RUNS and fails
/// loudly at the native boundary (SD0211) instead of skipping (the CI restore-failure
/// net; see <see cref="NativeRequirement"/>).
/// </summary>
public sealed class FnaFactAttribute : FactAttribute
{
    public FnaFactAttribute()
    {
        if (NativeRequirement.ShouldSkip(
                FnaTestGate.Vkd3dAvailable,
                Environment.GetEnvironmentVariable(NativeRequirement.Vkd3dEnvVar)))
            Skip = FnaTestGate.SkipReason;
    }
}

/// <summary>
/// An xUnit theory that skips when the vkd3d-shader native library is absent — unless
/// <c>SHADOWDUSK_REQUIRE_VKD3D</c> is set (then it runs and fails loudly; see
/// <see cref="FnaFactAttribute"/>).
/// </summary>
public sealed class FnaTheoryAttribute : TheoryAttribute
{
    public FnaTheoryAttribute()
    {
        if (NativeRequirement.ShouldSkip(
                FnaTestGate.Vkd3dAvailable,
                Environment.GetEnvironmentVariable(NativeRequirement.Vkd3dEnvVar)))
            Skip = FnaTestGate.SkipReason;
    }
}
