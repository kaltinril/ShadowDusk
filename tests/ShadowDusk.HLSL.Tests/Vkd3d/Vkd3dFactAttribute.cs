#nullable enable

using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// An xUnit <see cref="FactAttribute"/> for vkd3d-shader live-compile tests. The
/// binding is cross-platform; whether the tests can run depends only on the native
/// vkd3d-shader library being present, so the gate is availability-probed (the
/// sibling of <c>ShadowDusk.Integration.Tests.FnaTestGate</c>) rather than OS-gated
/// — once tools/restore provisions the per-RID binary (Phase 37 C), these run on
/// every OS. The off-platform "fail loudly" path (SD0211) is a separate, pure concern.
/// DXBC reflection itself is pure managed since Phase 18 Track A (<c>RdefReader</c>) and
/// needs no gate; tests that exercise the <c>D3DReflect</c> TEST ORACLE (d3dcompiler_47,
/// e.g. <c>DxbcReflectionParityTests</c>) set <paramref name="requiresD3DReflect"/> so
/// they skip truthfully off-Windows instead of failing.
/// </summary>
public sealed class Vkd3dFactAttribute : FactAttribute
{
    public Vkd3dFactAttribute(bool requiresD3DReflect = false)
    {
        if (!Vkd3dTestGate.Available)
            Skip = Vkd3dTestGate.SkipReason;
        else if (requiresD3DReflect && !OperatingSystem.IsWindows())
            Skip = "The D3DReflect test oracle P/Invokes d3dcompiler_47 — Windows-only " +
                   "(the product's managed DXBC reflection runs everywhere; only the " +
                   "oracle comparison needs Windows).";
    }
}

/// <summary>
/// Availability probe for the native vkd3d-shader library, mirroring
/// <c>Vkd3dLoader</c>'s file probes: the build-output copy next to the test
/// binaries, then tools/vkd3d/ walking up toward the repo root. On macOS the
/// restored layout is per-arch (osx-x64/ / osx-arm64/ subdirectories — both arches
/// share one dylib file name), so the current process arch's subdirectory is
/// probed before the flat path.
/// </summary>
internal static class Vkd3dTestGate
{
    internal const string SkipReason =
        "vkd3d-shader native library not found (restore it via tools/restore — " +
        "see plan/PHASE-37-cross-platform-native-availability.md, finding C).";

    internal static bool Available { get; } = Probe();

    private static bool Probe()
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
