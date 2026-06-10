#nullable enable

using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.Integration.Tests.Tests;

/// <summary>
/// Skip gate for the FNA fx_2_0 integration tests: every FNA compile goes through the
/// native vkd3d-shader SM1–3 backend, and that library is a RESTORED, non-redistributed
/// artifact (tools/restore is non-fatal when it's absent — the known Phase 37 C gap, so
/// CI typically runs without it). When the library can't be found these tests skip with
/// a clear reason instead of failing the run — the sibling of
/// <c>ShadowDusk.HLSL.Tests.Vkd3d.Vkd3dFactAttribute</c>, but availability-probed rather
/// than OS-gated so a future restored Linux/macOS binary enables them automatically.
/// </summary>
internal static class FnaTestGate
{
    internal const string SkipReason =
        "vkd3d-shader native library not found (restore it via tools/restore — " +
        "see plan/PHASE-37-cross-platform-native-availability.md, finding C).";

    internal static bool Vkd3dAvailable { get; } = ProbeVkd3d();

    // Mirrors Vkd3dLoader's probe: the build-output copy next to the test binaries,
    // then tools/vkd3d/ walking up from the output directory toward the repo root.
    private static bool ProbeVkd3d()
    {
        foreach (string name in CandidateFileNames())
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, name)))
                return true;

            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "vkd3d", name)))
                    return true;
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
}

/// <summary>An xUnit fact that skips when the vkd3d-shader native library is absent.</summary>
public sealed class FnaFactAttribute : FactAttribute
{
    public FnaFactAttribute()
    {
        if (!FnaTestGate.Vkd3dAvailable)
            Skip = FnaTestGate.SkipReason;
    }
}

/// <summary>An xUnit theory that skips when the vkd3d-shader native library is absent.</summary>
public sealed class FnaTheoryAttribute : TheoryAttribute
{
    public FnaTheoryAttribute()
    {
        if (!FnaTestGate.Vkd3dAvailable)
            Skip = FnaTestGate.SkipReason;
    }
}
