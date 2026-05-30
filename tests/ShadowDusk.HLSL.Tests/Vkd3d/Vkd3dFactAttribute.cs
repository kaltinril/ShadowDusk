#nullable enable

using System.Runtime.InteropServices;
using Xunit;

namespace ShadowDusk.HLSL.Tests.Vkd3d;

/// <summary>
/// An xUnit <see cref="FactAttribute"/> for vkd3d-shader live-compile tests. The
/// binding is cross-platform, but the native vkd3d-shader library is currently only
/// built for win-x64 (restored into tools/vkd3d/ and copied next to the binaries).
/// Off Windows the lib is absent, so these tests are skipped there — the
/// off-platform "fail loudly" path (SD0211) is a separate, pure concern.
/// </summary>
public sealed class Vkd3dFactAttribute : FactAttribute
{
    public Vkd3dFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "vkd3d-shader native library only available for win-x64 locally.";
    }
}
